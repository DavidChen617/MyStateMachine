using System.Net.Sockets;
using System.Runtime.CompilerServices;

MyStateMachine.Log("Main: starting state machine..");

MyStateMachine stateMachine = new()
{
    Builder = AsyncTaskMethodBuilder.Create(),
    State = -1,
};

stateMachine.Builder.Start(ref stateMachine);

Task work = stateMachine.Builder.Task;

MyStateMachine.Log("Main: Start() returned, free to do other work");

while (!work.IsCompleted)
{
    MyStateMachine.Log("Main: doing other work...");
    Thread.Sleep(300);
}

work.GetAwaiter().GetResult();

MyStateMachine.Log("Main: done");

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

static class FakeNetwork
{
    record Pending(DateTimeOffset ArrivedOnUtc, string Request, TaskCompletionSource<string> Tcs);
    static readonly List<Pending> InFlight = [];
    static FakeNetwork()
    {
        new Thread(IoLoop) { IsBackground = true, Name = "IO" }.Start();
    }

    public static Task<string> SendAsync(string request)
    {
        var latency = Random.Shared.Next(500, 2000);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (InFlight)
            InFlight.Add(new Pending(DateTimeOffset.UtcNow.AddMilliseconds(latency), request, tcs));

        MyStateMachine.Log($" [NIC] Sent {request}, response expected in {latency}ms");
        return tcs.Task;
    }

    static void IoLoop(object? obj)
    {
        while (true)
        {
            List<Pending> arrived;
            lock (InFlight)
            {
                arrived = [.. InFlight.Where(p => p.ArrivedOnUtc <= DateTimeOffset.UtcNow)];
                InFlight.RemoveAll(arrived.Contains);
            }

            foreach (var p in arrived)
            {

                if (Random.Shared.Next(10) == 0)
                {
                    MyStateMachine.Log($" [IO] {p.Request} connection reset -> SetException");
                    p.Tcs.SetException(new SocketException((int)SocketError.ConnectionReset));
                }
                else
                {
                    MyStateMachine.Log($" [IO] {p.Request} packet arrived -> SetResult");
                    p.Tcs.SetResult($"{p.Request} response");
                }

                MyStateMachine.Log($" [IO] SetException/SetResult returned");
            }

            Thread.Sleep(10);
        }
    }
}
