using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

PrintAsync().Wait();
static async MyTask PrintAsync()
{
    for (int i = 0; i < 5; i++)
    {
        Console.WriteLine(i);
        await MyTask.Delay(1000);
    }
}

//MyTask.Iterate(PrintAsync()).Wait();
//static IEnumerable<MyTask> PrintAsync()
//{
//    for (int i = 0; i < 5; i++)
//    {
//        Console.WriteLine(i);
//        yield return MyTask.Delay(1000);
//    }
//}

//Console.WriteLine("Hello, ");
//MyTask.Delay(2000).ContinueWith(() => Console.WriteLine("World!"))
//.Wait();

//Console.WriteLine("Hello, ");
//MyTask.Delay(2000).ContinueWith(delegate
//{
//    Console.WriteLine("World!");
//    return MyTask.Delay(2000).ContinueWith(delegate
//    {
//        Console.WriteLine("World!");
//        return MyTask.Delay(2000).ContinueWith(delegate
//        {
//            Console.WriteLine("World!");
//        });
//    });
//})
//.Wait();

//AsyncLocal<int> asyncLocalValue = new AsyncLocal<int>();
//for (int i = 0; i < 100; i++)
//{
//    asyncLocalValue.Value = i; // Capture the current value of i
//    MyThreadPool.QueueUserWorkItem(delegate
//    {
//        Console.WriteLine(asyncLocalValue.Value);
//        Thread.Sleep(1000);
//    });
//}
//Console.ReadLine();

//AsyncLocal<int> asyncLocalValue = new AsyncLocal<int>();
//var tasks = new List<MyTask>();
//for (int i = 0; i < 100; i++)
//{
//    asyncLocalValue.Value = i; // Capture the current value of i
//    tasks.Add(MyTask.Run(delegate
//    {
//        Console.WriteLine(asyncLocalValue.Value);
//        Thread.Sleep(1000);
//    }));
//}
//foreach (var task in tasks)
//{
//    task.Wait();
//}
//MyTask.WhenAll(tasks).Wait();

[AsyncMethodBuilder(typeof(MyTaskMethodBuilder))]
class MyTask
{
    private bool _isCompleted;
    private Exception? _exception;
    private Action? _continuation;
    private ExecutionContext _context;

    public struct Awaiter(MyTask task) : INotifyCompletion
    {
        public Awaiter GetAwaiter() => this;
        public bool IsCompleted => task.IsCompleted;
        public void OnCompleted(Action continuation) => task.ContinueWith(continuation);
        public void GetResult() => task.Wait();
    }
    public Awaiter GetAwaiter() => new Awaiter(this);
    public bool IsCompleted
    {
        get
        {
            lock (this)
            {
                return _isCompleted;
            }
        }
        private set;
    }
    private void Complete(Exception? exception)
    {
        lock (this)
        {
            if (_isCompleted) throw new InvalidOperationException("Stop messing with my code.");
            _isCompleted = true;
            _exception = exception;
            if (_continuation != null)
            {
                if (_context != null)
                {
                    //ExecutionContext.Run(_context, _ => _continuation(), null);
                    ExecutionContext.Run(_context, (object? state) => ((Action)state!).Invoke(), _continuation);

                }
                else
                {
                    _continuation();
                }
            }
        }
    }
    public void SetResult() 
    {
        Complete(null);
    }
    public void SetException(Exception ex)
    {
        Complete(ex);
    }
    public void Wait()
    {
        ManualResetEventSlim waitHandle = null;
        lock (this)
        {
            if (!_isCompleted)
            {
                waitHandle = new ManualResetEventSlim();
                ContinueWith(waitHandle.Set);
            }
        }
        waitHandle?.Wait();
        if (_exception != null)
        {
            //throw new Exception("Task failed.", _exception);
            //throw new AggregateException(_exception);
            ExceptionDispatchInfo.Capture(_exception).Throw();
        }
    }
    public MyTask ContinueWith(Action continuation)
    {
        MyTask task = new MyTask();
        Action callBack = () =>
        {
            try
            {   continuation();
                task.SetResult();
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
        };
        lock (this)
        {
            if (_isCompleted)
            {
                MyThreadPool.QueueUserWorkItem(callBack);
            }
            else
            {
                _continuation = callBack;
                _context = ExecutionContext.Capture();
            }
        }
        return task;

    }

    public MyTask ContinueWith(Func<MyTask> continuation)
    {
        MyTask task = new MyTask();
        Action callBack = () =>
        {
            try
            {
                MyTask nextTask = continuation();
                nextTask.ContinueWith(() =>
                {
                    if (nextTask._exception != null)
                    {
                        task.SetException(nextTask._exception);
                    }
                    else
                    {
                        task.SetResult();
                    }
                });
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
        };
        lock (this)
        {
            if (_isCompleted)
            {
                MyThreadPool.QueueUserWorkItem(callBack);
            }
            else
            {
                _continuation = callBack;
                _context = ExecutionContext.Capture();
            }
        }
        return task;

    }

    public static MyTask Run(Action action)
    {
        MyTask task = new MyTask();

        MyThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
            task.SetResult();
        });

        return task;
    }

    public static MyTask WhenAll(List<MyTask> tasks)
    {
        MyTask allDone = new MyTask();

        int count = tasks.Count;
        if (count == 0)
        {
            allDone.SetResult();
            return allDone;
        }
        Action continuation = () =>
        {
            if (Interlocked.Decrement(ref count) == 0)
            {
                allDone.SetResult();
            }
        };
        foreach(MyTask task in tasks)
        {
            task.ContinueWith(continuation);
        }

        return allDone;
    }

    public static MyTask Delay(long delay)
    {
        MyTask task = new MyTask();

        new Timer(_ => task.SetResult(), null, delay, Timeout.Infinite);

        return task;
    }

    public static MyTask Iterate(IEnumerable<MyTask> tasks)
    {
        MyTask task = new MyTask();
        IEnumerator<MyTask> enumerator = tasks.GetEnumerator(); // TODO: dispose

        void MoveNext()
        {
            try
            {
                while (enumerator.MoveNext())
                {
                    MyTask next = enumerator.Current;
                    if (next.IsCompleted)
                    {
                        next.Wait();
                        continue;
                    }
                    next.ContinueWith(MoveNext);
                    return;
                }
            }
            catch(Exception e)
            {
                task.SetException(e);
                return;
            }
            task.SetResult();
        }
        MoveNext();
        return task;
    }
}
class MyTaskMethodBuilder
{
    public MyTask Task { get; } = new MyTask();

    public static MyTaskMethodBuilder Create() => new MyTaskMethodBuilder();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => stateMachine.MoveNext();

    public void SetResult() => Task.SetResult();
    public void SetException(Exception ex) => Task.SetException(ex);
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(stateMachine.MoveNext);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(stateMachine.MoveNext);
}

static class MyThreadPool
{
    private static readonly BlockingCollection<(Action, ExecutionContext?)> _workItems = new ();
    public static void QueueUserWorkItem(Action work) // WaitCallback is a delegate that takes an object parameter, but for simplicity, we use Action here
    {
        _workItems.Add((work, ExecutionContext.Capture()));
    }
    static MyThreadPool()
    {
        // Start a fixed number of worker threads to process the work items
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            Thread workerThread = new Thread(() =>
            {
                while (true)
                {
                    (Action workItem, ExecutionContext? context) = _workItems.Take(); // This will block until a work item is available
                    try
                    {
                        if (context == null) 
                        {
                            workItem();
                        }
                        else
                        {
                            //ExecutionContext.Run(context, _ => workItem(), null);
                            ExecutionContext.Run(context, (object? state) => ((Action)state!).Invoke(), workItem);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error executing work item: {ex}");
                    }
                }

            });
            workerThread.IsBackground = true; // Ensure the thread doesn't prevent application exit
            workerThread.Start();
        }
    }
}