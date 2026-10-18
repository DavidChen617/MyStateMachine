# async / await 如何被編譯器改寫

2002 年，.NET 1.0 的非同步長這樣：`BeginRead`、`EndRead`，外加一個 `IAsyncResult`。這叫 APM，每個非同步操作都要拆成兩個方法，再接一個 callback。

2005 年，.NET 2.0 改用事件：呼叫 `DownloadStringAsync`，等 `DownloadStringCompleted` 觸發。這叫 EAP，少了一個方法，多了一個事件。

2010 年，.NET 4 推出 `Task`。2012 年，C# 5 加上 `async` / `await`。原本散在 callback 裡的邏輯，終於能照順序一行一行寫下來，跟同步程式碼幾乎一樣。

十年來，大家都在想辦法把非同步寫得更短。這篇反過來，把 `await` 拆回去。

看完之後，你會知道寫下 `await` 時編譯器產生了什麼、執行緒去了哪裡，以及方法怎麼從停下的地方接著執行。

先說清楚範圍：真正的 `AsyncTaskMethodBuilder` 還要處理 `ExecutionContext`、`SynchronizationContext`、`ValueTask` 和各種最佳化。這篇不碰這些，只看最核心的部分：狀態機怎麼掛起、怎麼恢復。

需要準備的東西：.NET SDK、一個能反編譯的工具（ILSpy、Rider，或線上的 SharpLab），還有一點耐心看 `<>1__state` 這種名字。

開始吧，先看一段再普通不過的 async 程式。

## 原始碼

```csharp
var s = await GetStringAsync();
Console.WriteLine(s);

var i = await GetIntAsync();
Console.WriteLine(i);

static Task<string> GetStringAsync() => Task.FromResult("Hello World!");
static Task<int> GetIntAsync() => Task.FromResult(1);
```

## 改寫後的結構

`async` / `await` 是語法糖。編譯器會把 async 方法拆成兩部分：

- stub 方法：建立並啟動狀態機。
- 狀態機類別：實作 `IAsyncStateMachine`，原本的方法本體搬進它的 `MoveNext`。

```
Program
├── <Main>(args)                    程式進入點，呼叫 <Main>$ 並等它跑完
├── <Main>$(args)                   stub：建立狀態機、Start、回傳 Task
├── <<Main>$>g__GetStringAsync|0_0  local function 變成靜態方法
├── <<Main>$>g__GetIntAsync|0_1
└── <<Main>$>d__0 : IAsyncStateMachine   狀態機，方法本體都在 MoveNext 裡
```

## 1. Stub 方法

原本的方法只負責啟動狀態機：

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

- 方法簽章沒變，仍回傳 `Task`，呼叫端不必知道內部實作。
- `Start` 在呼叫端的執行緒上直接執行第一次 `MoveNext`，執行到第一個未完成的 await 才返回。
- 呼叫端拿到的就是 `Builder.Task`。狀態機最後呼叫 `SetResult` 或 `SetException`，這個 Task 才完成。

Top-level statements 的進入點 `<Main>` 把 `<Main>$` 回傳的 Task 交給 `AsyncHelpers.HandleAsyncEntryPoint`，這是這次反編譯看到的寫法。我印象中較舊的編譯器會產生 `<Main>$(args).GetAwaiter().GetResult()`，但沒查到哪個版本改的。

## 2. 狀態機的欄位

| 欄位 | 用途 |
|---|---|
| `<>1__state` | 目前狀態：`-1` 執行中，`0` 停在第 1 個 await，`1` 停在第 2 個 await，`-2` 已結束 |
| `<>t__builder` | `AsyncTaskMethodBuilder`，負責建立 Task、註冊 continuation、設定結果 |
| `args` | 方法參數 |
| `<s>5__1`、`<i>5__2` | 原始碼的區域變數 `s`、`i`，提升（hoist）成欄位 |
| `<>s__3`、`<>s__4` | 編譯器加的暫存變數，接住 `GetResult()` 的回傳值 |
| `<>u__1`、`<>u__2` | 每個 await 各有一個 awaiter，掛起時存入，恢復時取出 |

每次呼叫 `MoveNext` 都是全新的方法呼叫，區域變數不會保留，要跨過 await 的值只能放在欄位。

Release 組態只提升真正跨過 await 的變數。這份反編譯的狀態機是 `class`，`s`、`i` 也都提升了，我因此判斷它是 Debug 組態的結果。

## 3. MoveNext

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

恢復時，`MoveNext` 先看 `<>1__state`，決定跳到哪裡：

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

`await` 後面的程式碼要等下一次呼叫 `MoveNext` 才執行。方法有 N 個 await，最多切成 N+1 段，分散在不同次的 `MoveNext` 呼叫裡。

反編譯結果裡的 `num2` 沒有用途。IL 裡是 `state = num2 = 0` 這種連鎖賦值，反編譯器還原時留下了它。

## 4. 結束

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

方法丟出的例外不會直接往外拋，而是存進 Task，等呼叫端 `await` 這個 Task 時再拋一次。

`<i>5__2` 是 `int`，沒有參考要釋放，所以不必清。

## 5. 這個例子沒有掛起過

`Task.FromResult` 回傳已完成的 Task，`IsCompleted` 一開始就是 `true`，兩個 `if (!awaiter.IsCompleted)` 都不會進去。

整個方法在第一次 `MoveNext` 就跑完：沒有掛起、沒換執行緒，也沒建立 continuation。這條路徑叫 fast path。

遇到未完成的 Task，才會呼叫 `AwaitUnsafeOnCompleted`。builder 這時把狀態機包進 heap 上的 box，再註冊成 Task 的 continuation。

## 6. Debug 和 Release

| | Debug | Release |
|---|---|---|
| 狀態機型別 | `class` | `struct` |
| 第一次掛起時 | 本來就在 heap 上 | builder 把 struct 複製進 box |
| 提升的區域變數 | 全部 | 只有跨 await 的 |

Release 用 struct，讓走 fast path 的呼叫完全不配置記憶體。

## 動手試

`Program.cs` 不用 `async` / `await`，照上面的樣板手寫 `MyStateMachine`，再用 `FakeNetwork` 模擬網路 I/O：封包「到達」時，IO 執行緒呼叫 `TaskCompletionSource.SetResult`。執行後能看到狀態機實際掛起、恢復，也看得到每段 `MoveNext` 在哪條執行緒上執行。

```bash
dotnet run
```

| 反編譯結果 | `Program.cs` |
|---|---|
| `<>1__state` | `State` |
| `<>t__builder` | `Builder` |
| `<>u__1`、`<>u__2` | `savedAwaiter1`、`savedAwaiter2` |
| `GetStringAsync()` / `GetIntAsync()` | `FakeNetwork.SendAsync(...)`，回傳未完成的 Task |

## 參考資料

- [Asynchronous programming patterns](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/)：官方文件，APM、EAP、TAP 三種模式
- [The history of C#](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-version-history)：官方文件，C# 5 加入 async / await
- [How Async/Await Really Works in C#](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/)：Microsoft 官方部落格，詳細拆解編譯器產生的狀態機
- [AsyncStateMachineAttribute](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.asyncstatemachineattribute)：官方文件
- [AsyncTaskMethodBuilder](https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.asynctaskmethodbuilder)：官方文件
- [AsyncTaskMethodBuilderT.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncTaskMethodBuilderT.cs)：dotnet/runtime 原始碼，box 在這裡建立
- [SharpLab](https://sharplab.io/)：非官方，貼上 C# 程式碼就能看編譯器改寫後的結果
