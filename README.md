# async / await 如何被編譯器改寫

2002 年，.NET 1.0 的非同步長這樣：`BeginRead`、`EndRead`，外加一個 `IAsyncResult`。這叫 APM，每個非同步操作都要拆成兩個方法，再接一個 callback。

2005 年，.NET 2.0 改用事件：呼叫 `DownloadStringAsync`，等 `DownloadStringCompleted` 觸發。這叫 EAP，少了一個方法，多了一個事件。

2010 年，.NET 4 推出 `Task`。2012 年，C# 5 加上 `async` / `await`。原本散在 callback 裡的邏輯，終於能照順序一行一行寫下來，跟同步程式碼幾乎一樣。

十年來，大家都在想辦法把非同步寫得更短。這篇反過來，把 `await` 拆回去。

看完之後，你會知道寫下 `await` 時編譯器產生了什麼、執行緒去了哪裡，以及方法怎麼從停下的地方接著執行。

先說清楚範圍：真正的 `AsyncTaskMethodBuilder` 還要處理 `ExecutionContext`、`SynchronizationContext`、`ValueTask` 和各種最佳化。這篇不碰這些，只看最核心的部分：狀態機怎麼掛起、怎麼恢復。

需要準備的東西：.NET SDK、一個能反編譯的工具（ILSpy、Rider，或線上的 SharpLab），還有一點耐心看 `<>1__state` 這種名字。

開始吧，先看一段再普通不過的 async 程式。

## 一段普通的 async 程式

```csharp
var s = await GetStringAsync();
Console.WriteLine(s);

var i = await GetIntAsync();
Console.WriteLine(i);

static Task<string> GetStringAsync() => Task.FromResult("Hello World!");
static Task<int> GetIntAsync() => Task.FromResult(1);
```

把編譯出來的 dll 丟進反編譯工具，就能看到編譯器產生的程式碼。以下是 Debug 組態的結果。

## 編譯器產生了什麼

```
Program
├── <Main>(args)                    程式進入點，呼叫 <Main>$ 並等它跑完
├── <Main>$(args)                   stub：建立狀態機、Start、回傳 Task
├── <<Main>$>g__GetStringAsync|0_0  local function 變成靜態方法
├── <<Main>$>g__GetIntAsync|0_1
└── <<Main>$>d__0 : IAsyncStateMachine   狀態機
```

原本的方法本體全部搬進狀態機的 `MoveNext`。

## Stub：只負責發動

```csharp
[AsyncStateMachine(typeof(Program.<<Main>$>d__0))]
private static Task <Main>$(string[] args)
{
    var stateMachine = new <<Main>$>d__0();
    stateMachine.<>t__builder = AsyncTaskMethodBuilder.Create();
    stateMachine.args = args;              // 參數複製進狀態機
    stateMachine.<>1__state = -1;          // -1：還沒開始或正在執行
    stateMachine.<>t__builder.Start(ref stateMachine);
    return stateMachine.<>t__builder.Task; // 呼叫端拿到的 Task
}
```

`Start` 在呼叫端的執行緒上同步執行第一次 `MoveNext`，直到碰上第一個未完成的 await 才返回。

## 狀態機的欄位：區域變數搬家

| 欄位 | 用途 |
|---|---|
| `<>1__state` | 目前狀態：`-1` 執行中，`0` 停在第 1 個 await，`1` 停在第 2 個 await，`-2` 已結束 |
| `<>t__builder` | `AsyncTaskMethodBuilder`，負責建立 Task、註冊 continuation、設定結果 |
| `args` | 方法參數 |
| `<s>5__1`、`<i>5__2` | 原始碼的區域變數 `s`、`i` |
| `<>s__3`、`<>s__4` | 編譯器加的暫存變數，接住 `GetResult()` 的回傳值 |
| `<>u__1`、`<>u__2` | 每個 await 各有一個 awaiter，掛起時存入，恢復時取出 |

每次恢復都是重新呼叫 `MoveNext`，區域變數不會保留，所以要跨過 await 的值都存成欄位。

## MoveNext：一個 await 拆成五步

每個 `await X` 都展開成同一個樣板：

```csharp
awaiter = X.GetAwaiter();
if (!awaiter.IsCompleted)
{
    <>1__state = N;                    // 1. 記下停在哪裡
    <>u__N = awaiter;                  // 2. 存 awaiter
    <>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref this);  // 3. 完成時再呼叫 MoveNext
    return;                            // 4. 離開，把執行緒還回去
}
ResumeN:
result = awaiter.GetResult();          // 5. 取結果；失敗時例外從這裡拋出
```

恢復時，`MoveNext` 開頭依 `<>1__state` 跳回原位：

```csharp
int num1 = <>1__state;
if (num1 != 0)
{
    if (num1 != 1)
    {
        // 第一次進來，從方法開頭執行
    }
    else
    {
        // state == 1：取回第 2 個 awaiter，goto label_9
    }
}
else
{
    // state == 0：取回第 1 個 awaiter，往下執行到 GetResult
}
```

方法有 N 個 await，最多切成 N+1 段，分散在不同次的 `MoveNext` 呼叫裡。

## 收尾：結果和例外都交給 builder

```csharp
catch (Exception ex)
{
    <>1__state = -2;
    <s>5__1 = null;               // 清掉參考型別欄位，GC 才能回收
    <>t__builder.SetException(ex); // Task 變成 Faulted
    return;
}
<>1__state = -2;
<s>5__1 = null;
<>t__builder.SetResult();          // Task 變成 RanToCompletion
```

例外不會直接往外拋，而是存進 Task，等呼叫端 `await` 時再拋出。

## 等等，這個例子根本沒停過

`Task.FromResult` 回傳已完成的 Task，兩個 `if (!awaiter.IsCompleted)` 都不會進去，整個方法在第一次 `MoveNext` 就跑完了。這叫 fast path。要看到掛起，得給 await 一個未完成的 Task。

## 讓它真的停下來

`Program.cs` 不用 `async` / `await`，手寫狀態機 `MyStateMachine`，等同於：

```csharp
async Task RunAsync()
{
    Log($"Got {await FakeNetwork.SendAsync("Get /a")}");
    Log($"Got {await FakeNetwork.SendAsync("Get /b")}");
}
```

### MyStateMachine

```csharp
class MyStateMachine : IAsyncStateMachine
{
    public int State { get; set; }

    public AsyncTaskMethodBuilder Builder;

    private TaskAwaiter<string> savedAwaiter1;

    private TaskAwaiter<string> savedAwaiter2;

    public void MoveNext()
    {
        Log($" MoveNext entered, State = {State}");
        int num1 = State;
        try
        {
            TaskAwaiter<string> awaiter1;
            TaskAwaiter<string> awaiter2;

            if (num1 != 0)
            {
                if (num1 != 1)
                {
                    Log("Sending request A");
                    awaiter1 = FakeNetwork.SendAsync("Get /a").GetAwaiter();
                    if (!awaiter1.IsCompleted)
                    {
                        State = 0;
                        savedAwaiter1 = awaiter1;
                        MyStateMachine stateMachine = this;
                        Log($"Suspended State = {State}, MoveNext() returns, thread released");
                        Builder.AwaitUnsafeOnCompleted(ref awaiter1, ref stateMachine);

                        return;
                    }
                }
                else
                {
                    awaiter2 = savedAwaiter2;
                    savedAwaiter2 = new TaskAwaiter<string>();
                    State = -1;
                    goto label9;
                }
            }
            else
            {
                awaiter1 = savedAwaiter1;
                savedAwaiter1 = new TaskAwaiter<string>();
                State = -1;
            }

            Log($"Got {awaiter1.GetResult()}");
            Log("Sending request B");
            awaiter2 = FakeNetwork.SendAsync("Get /b").GetAwaiter();
            if (!awaiter2.IsCompleted)
            {
                State = 1;
                savedAwaiter2 = awaiter2;
                MyStateMachine stateMachine = this;
                Log($"Suspended State = {State}");
                Builder.AwaitUnsafeOnCompleted(ref awaiter2, ref stateMachine);
                return;
            }

        label9:
            Log($"Got {awaiter2.GetResult()}");
        }
        catch (Exception e)
        {
            State = -2;
            Builder.SetException(e);

            return;
        }

        State = -2;
        Builder.SetResult();
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }
    public static void Log(string msg) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {msg}");
}
```

Main 負責建立狀態機並啟動：

```csharp
MyStateMachine stateMachine = new()
{
    Builder = AsyncTaskMethodBuilder.Create(),
    State = -1,
};
stateMachine.Builder.Start(ref stateMachine);
Task work = stateMachine.Builder.Task;
```

### 和編譯器版本的差異

- 沒有 stub 方法，Main 直接建立並啟動狀態機，所以 `State` 和 `Builder` 必須是公開的。
- 沒有提升的區域變數。`GetResult()` 的結果直接印出，所以沒有 `<s>5__1` 這類欄位。
- 多了 `Log` 呼叫，方便觀察執行順序。
- Main 用 `while` 輪詢 `IsCompleted`，沒有用 `await`，目的是在輸出裡看到 T1 在做別的事。

### 執行

```bash
dotnet run
```

輸出（節錄）：

```
[T1]  MoveNext entered, State = -1
[T1] Sending request A
[T1]  [NIC] Sent Get /a, response expected in 676ms
[T1] Suspended State = 0, MoveNext() returns, thread released
[T1] Main: Start() returned, free to do other work
[T1] Main: doing other work...
[T1] Main: doing other work...
[T4]  [IO] Get /a packet arrived -> SetResult
[T4]  [IO] SetException/SetResult returned
[T5]  MoveNext entered, State = 0
[T5] Got Get /a response
[T5] Sending request B
[T5] Suspended State = 1
[T1] Main: doing other work...
[T4]  [IO] Get /b packet arrived -> SetResult
[T5]  MoveNext entered, State = 1
[T5] Got Get /b response
[T1] Main: done
```

T1 掛起後回去做別的事，等待期間沒有執行緒卡在請求上；T4（IO）呼叫 `SetResult` 後，由 thread pool 的 T5 接著執行 `MoveNext`。

## 參考資料

- [Asynchronous programming patterns](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/)：官方文件，APM、EAP、TAP 三種模式
- [The history of C#](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-version-history)：官方文件，C# 5 加入 async / await
- [How Async/Await Really Works in C#](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/)：Microsoft 官方部落格，詳細拆解編譯器產生的狀態機
- [AsyncStateMachineAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.asyncstatemachineattribute)：官方文件
- [AsyncTaskMethodBuilder](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.asynctaskmethodbuilder)：官方文件
- [AsyncTaskMethodBuilderT.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs)：dotnet/runtime 原始碼，box 在這裡建立
- [TaskCreationOptions.RunContinuationsAsynchronously](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskcreationoptions)：官方文件
- [SharpLab](https://sharplab.io/)：非官方，貼上 C# 程式碼就能看編譯器改寫後的結果
