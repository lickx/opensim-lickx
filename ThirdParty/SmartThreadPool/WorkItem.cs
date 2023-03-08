using System;
using System.Diagnostics;
using System.Threading;

namespace Amib.Threading.Internal
{
    /// <summary>
    /// Holds a callback delegate and the state for that delegate.
    /// </summary>
    public partial class WorkItem
    {
        #region WorkItemState enum

        /// <summary>
        /// Indicates the state of the work item in the thread pool
        /// </summary>
        private enum WorkItemState
        {
            InQueue = 0,    // Nexts: InProgress, Canceled
            InProgress = 1,    // Nexts: Completed, Canceled
            Completed = 2,    // Stays Completed
            Canceled = 3,    // Stays Canceled
        }

        private static bool IsValidStatesTransition(WorkItemState currentState, WorkItemState nextState)
        {
            bool valid = false;

            switch (currentState)
            {
                case WorkItemState.InQueue:
                    valid = (WorkItemState.InProgress == nextState) || (WorkItemState.Canceled == nextState);
                    break;
                case WorkItemState.InProgress:
                    valid = (WorkItemState.Completed == nextState) || (WorkItemState.Canceled == nextState);
                    break;
                case WorkItemState.Completed:
                case WorkItemState.Canceled:
                    // Cannot be changed
                    break;
                default:
                    // Unknown state
                    Debug.Assert(false);
                    break;
            }

            return valid;
        }

        #endregion

        #region Fields

        /// <summary>
        /// Callback delegate for the callback.
        /// </summary>
        private WorkItemCallback m_callback;
        private WaitCallback m_callbackNoResult;

        /// <summary>
        /// State with which to call the callback delegate.
        /// </summary>
        private object m_state;

        /// <summary>
        /// Stores the caller's context
        /// </summary>
        private  ExecutionContext m_callerContext = null;

        /// <summary>
        /// Holds the result of the mehtod
        /// </summary>
        private object m_result;

        /// <summary>
        /// Hold the exception if the method threw it
        /// </summary>
        private Exception m_exception;

        /// <summary>
        /// Hold the state of the work item
        /// </summary>
        private WorkItemState m_workItemState;

        /// <summary>
        /// A ManualResetEvent to indicate that the result is ready
        /// </summary>
        private ManualResetEvent m_workItemCompleted;

        /// <summary>
        /// A reference count to the _workItemCompleted. 
        /// When it reaches to zero _workItemCompleted is Closed
        /// </summary>
        private int m_workItemCompletedRefCount;

        /// <summary>
        /// Represents the result state of the work item
        /// </summary>
        private readonly WorkItemResult m_workItemResult;

        /// <summary>
        /// Work item info
        /// </summary>
        private readonly WorkItemInfo m_workItemInfo;

        /// <summary>
        /// Called when the WorkItem starts
        /// </summary>
        private event WorkItemStateCallback m_workItemStartedEvent;

        /// <summary>
        /// Called when the WorkItem completes
        /// </summary>
        private event WorkItemStateCallback m_workItemCompletedEvent;

        /// <summary>
        /// A reference to an object that indicates whatever the 
        /// WorkItemsGroup has been canceled
        /// </summary>
        private CanceledWorkItemsGroup m_canceledWorkItemsGroup = CanceledWorkItemsGroup.NotCanceledWorkItemsGroup;

        /// <summary>
        /// A reference to an object that indicates whatever the 
        /// SmartThreadPool has been canceled
        /// </summary>
        private CanceledWorkItemsGroup m_canceledSmartThreadPool = CanceledWorkItemsGroup.NotCanceledWorkItemsGroup;

        /// <summary>
        /// The work item group this work item belong to.
        /// </summary>
        private readonly IWorkItemsGroup m_workItemsGroup;

        /// <summary>
        /// The thread that executes this workitem.
        /// This field is available for the period when the work item is executed, before and after it is null.
        /// </summary>
        private Thread m_executingThread;

        /// <summary>
        /// The absulote time when the work item will be timeout
        /// </summary>
        private long m_expirationTime;

        #region Performance Counter fields

        /// <summary>
        /// Stores how long the work item waited on the stp queue
        /// </summary>
        private Stopwatch _waitingOnQueueStopwatch;

        /// <summary>
        /// Stores how much time it took the work item to execute after it went out of the queue
        /// </summary>
        private Stopwatch _processingStopwatch;

        #endregion

        #endregion

        #region Properties

        public TimeSpan WaitingTime
        {
            get
            {
                return _waitingOnQueueStopwatch.Elapsed;
            }
        }

        public TimeSpan ProcessTime
        {
            get
            {
                return _processingStopwatch.Elapsed;
            }
        }

        internal WorkItemInfo WorkItemInfo
        {
            get
            {
                return m_workItemInfo;
            }
        }

        #endregion

        #region Construction

        /// <summary>
        /// Initialize the callback holding object.
        /// </summary>
        /// <param name="workItemsGroup">The workItemGroup of the workitem</param>
        /// <param name="workItemInfo">The WorkItemInfo of te workitem</param>
        /// <param name="callback">Callback delegate for the callback.</param>
        /// <param name="state">State with which to call the callback delegate.</param>
        /// 
        /// We assume that the WorkItem object is created within the thread
        /// that meant to run the callback
        public WorkItem(IWorkItemsGroup workItemsGroup, WorkItemInfo workItemInfo, WorkItemCallback callback, object state)
        {
            m_workItemsGroup = workItemsGroup;
            m_workItemInfo = workItemInfo;

            if (m_workItemInfo.UseCallerCallContext && !ExecutionContext.IsFlowSuppressed())
            {
                ExecutionContext ec = ExecutionContext.Capture();
                if (ec is not null)
                {
                    m_callerContext = ec.CreateCopy();
                    ec.Dispose();
                    ec = null;
                }
            }

            m_callback = callback;
            m_callbackNoResult = null;
            m_state = state;
            m_workItemResult = new WorkItemResult(this);
            Initialize();
        }

        public WorkItem(IWorkItemsGroup workItemsGroup, WorkItemInfo workItemInfo, WaitCallback callback, object state)
        {
            m_workItemsGroup = workItemsGroup;
            m_workItemInfo = workItemInfo;

            if (m_workItemInfo.UseCallerCallContext && !ExecutionContext.IsFlowSuppressed())
            {
                ExecutionContext ec = ExecutionContext.Capture();
                if (ec is not null)
                {
                    m_callerContext = ec.CreateCopy();
                    ec.Dispose();
                    ec = null;
                }
            }

            m_callbackNoResult = callback;
            m_state = state;
            m_workItemResult = new WorkItemResult(this);
            Initialize();
        }

        internal void Initialize()
        {
            // The _workItemState is changed directly instead of using the SetWorkItemState
            // method since we don't want to go throught IsValidStateTransition.
            m_workItemState = WorkItemState.InQueue;

            m_workItemCompleted = null;
            m_workItemCompletedRefCount = 0;
            _waitingOnQueueStopwatch = new Stopwatch();
            _processingStopwatch = new Stopwatch();
            m_expirationTime = m_workItemInfo.Timeout > 0 ? DateTime.UtcNow.Ticks + m_workItemInfo.Timeout * TimeSpan.TicksPerMillisecond :  long.MaxValue;
        }

        internal bool WasQueuedBy(IWorkItemsGroup workItemsGroup)
        {
            return (workItemsGroup == m_workItemsGroup);
        }


        #endregion

        #region Methods

        internal CanceledWorkItemsGroup CanceledWorkItemsGroup
        {
            get { return m_canceledWorkItemsGroup; }
            set { m_canceledWorkItemsGroup = value; }
        }

        internal CanceledWorkItemsGroup CanceledSmartThreadPool
        {
            get { return m_canceledSmartThreadPool; }
            set { m_canceledSmartThreadPool = value; }
        }

        /// <summary>
        /// Change the state of the work item to in progress if it wasn't canceled.
        /// </summary>
        /// <returns>
        /// Return true on success or false in case the work item was canceled.
        /// If the work item needs to run a post execute then the method will return true.
        /// </returns>
        public bool StartingWorkItem()
        {
            _waitingOnQueueStopwatch.Stop();
            _processingStopwatch.Start();

            lock (this)
            {
                if (IsCanceled)
                {
                    if ((m_workItemInfo.PostExecuteWorkItemCallback is not null) &&
                        ((m_workItemInfo.CallToPostExecute & CallToPostExecute.WhenWorkItemCanceled) == CallToPostExecute.WhenWorkItemCanceled))
                    {
                        return true;
                    }

                    return false;
                }

                Debug.Assert(WorkItemState.InQueue == GetWorkItemState());

                // No need for a lock yet, only after the state has changed to InProgress
                m_executingThread = Thread.CurrentThread;

                SetWorkItemState(WorkItemState.InProgress);
            }

            return true;
        }

        /// <summary>
        /// Execute the work item and the post execute
        /// </summary>
        public void Execute()
        {
            CallToPostExecute currentCallToPostExecute = 0;

            // Execute the work item if we are in the correct state
            switch (GetWorkItemState())
            {
                case WorkItemState.InProgress:
                    currentCallToPostExecute |= CallToPostExecute.WhenWorkItemNotCanceled;
                    ExecuteWorkItem();
                    break;
                case WorkItemState.Canceled:
                    currentCallToPostExecute |= CallToPostExecute.WhenWorkItemCanceled;
                    break;
                default:
                    Debug.Assert(false);
                    throw new NotSupportedException();
            }

            // Run the post execute as needed
            if ((currentCallToPostExecute & m_workItemInfo.CallToPostExecute) != 0)
            {
                PostExecute();
            }

            _processingStopwatch.Stop();
        }

        internal void FireWorkItemCompleted()
        {
            try
            {
                m_workItemCompletedEvent?.Invoke(this);
            }
            catch // Suppress exceptions
            { }
        }

        internal void FireWorkItemStarted()
        {
            try
            {
                m_workItemStartedEvent?.Invoke(this);
            }
            catch // Suppress exceptions
            { }
        }

        /// <summary>
        /// Execute the work item
        /// </summary>
        private void ExecuteWorkItem()
        {
            Exception exception = null;
            object result = null;

            try
            {
                try
                {
                    if(m_callbackNoResult is null)
                    {
                        if(m_callerContext is null)
                            result = m_callback(m_state);
                        else
                        {
                            ContextCallback _ccb = new( o => { result =m_callback(o); });
                            ExecutionContext.Run(m_callerContext, _ccb, m_state);
                        }
                    }
                    else
                    {
                        if (m_callerContext is null)
                            m_callbackNoResult(m_state);
                        else
                        {
                            ContextCallback _ccb = new(o => { m_callbackNoResult(o); });
                            ExecutionContext.Run(m_callerContext, _ccb, m_state);
                        }
                    }
                }
                catch (Exception e)
                {
                    // Save the exception so we can rethrow it later
                    exception = e;
                }

                // Remove the value of the execution thread, so it will be impossible to cancel the work item,
                // since it is already completed.
                // Cancelling a work item that already completed may cause the abortion of the next work item!!!
                Thread executionThread = Interlocked.CompareExchange(ref m_executingThread, null, m_executingThread);

                if (executionThread is null)
                {
                    // Oops! we are going to be aborted..., Wait here so we can catch the ThreadAbortException
                    Thread.Sleep(60 * 1000);

                    // If after 1 minute this thread was not aborted then let it continue working.
                }
            }
            // We must treat the ThreadAbortException or else it will be stored in the exception variable
            catch (ThreadAbortException tae)
            {
                // Check if the work item was cancelled
                // If we got a ThreadAbortException and the STP is not shutting down, it means the 
                // work items was cancelled.
                tae.GetHashCode();
                //if (!SmartThreadPool.CurrentThreadEntry.AssociatedSmartThreadPool.IsShuttingdown)
                //{
                //    Thread.ResetAbort();
                //}
            }
            if (!SmartThreadPool.IsWorkItemCanceled)
            {
                SetResult(result, exception);
            }
        }

        /// <summary>
        /// Runs the post execute callback
        /// </summary>
        private void PostExecute()
        {
            if (m_workItemInfo.PostExecuteWorkItemCallback is not null)
            {
                try
                {
                    m_workItemInfo.PostExecuteWorkItemCallback(m_workItemResult);
                }
                catch (Exception e)
                {
                    Debug.Assert(e is not null);
                }
            }
        }

        /// <summary>
        /// Set the result of the work item to return
        /// </summary>
        /// <param name="result">The result of the work item</param>
        /// <param name="exception">The exception that was throw while the workitem executed, null
        /// if there was no exception.</param>
        internal void SetResult(object result, Exception exception)
        {
            m_result = result;
            m_exception = exception;
            SignalComplete(false);
        }

        /// <summary>
        /// Returns the work item result
        /// </summary>
        /// <returns>The work item result</returns>
        internal IWorkItemResult GetWorkItemResult()
        {
            return m_workItemResult;
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// true when every work item in waitableResults has completed; otherwise false.
        /// </returns>
        internal static bool WaitAll( IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext,
            WaitHandle cancelWaitHandle)
        {
            if (0 == waitableResults.Length)
            {
                return true;
            }

            bool success;
            WaitHandle[] waitHandles = new WaitHandle[waitableResults.Length];
            GetWaitHandles(waitableResults, waitHandles);

            if ((cancelWaitHandle is null) && (waitHandles.Length <= 64))
            {
                success = STPEventWaitHandle.WaitAll(waitHandles, millisecondsTimeout, exitContext);
            }
            else
            {
                success = true;
                int millisecondsLeft = millisecondsTimeout;
                Stopwatch stopwatch = Stopwatch.StartNew();

                WaitHandle[] whs = cancelWaitHandle is null ?
                        new WaitHandle[] { null } :
                        new WaitHandle[] { null, cancelWaitHandle };

                bool waitInfinitely = (Timeout.Infinite == millisecondsTimeout);
                // Iterate over the wait handles and wait for each one to complete.
                // We cannot use WaitHandle.WaitAll directly, because the cancelWaitHandle
                // won't affect it.
                // Each iteration we update the time left for the timeout.
                for (int i = 0; i < waitableResults.Length; ++i)
                {
                    // WaitAny don't work with negative numbers
                    if (!waitInfinitely && (millisecondsLeft < 0))
                    {
                        success = false;
                        break;
                    }

                    whs[0] = waitHandles[i];
                    int result = STPEventWaitHandle.WaitAny(whs, millisecondsLeft, exitContext);
                    if ((result > 0) || (STPEventWaitHandle.WaitTimeout == result))
                    {
                        success = false;
                        break;
                    }

                    if (!waitInfinitely)
                    {
                        // Update the time left to wait
                        millisecondsLeft = millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds;
                    }
                }
            }
            // Release the wait handles
            ReleaseWaitHandles(waitableResults);

            return success;
        }

        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if no work item result satisfied the wait and a time interval equivalent to millisecondsTimeout has passed or the work item has been canceled.
        /// </returns>
        internal static int WaitAny( IWaitableResult[] waitableResults, int millisecondsTimeout,
            bool exitContext, WaitHandle cancelWaitHandle)
        {
            WaitHandle[] waitHandles;
            if (cancelWaitHandle is not null)
            {
                waitHandles = new WaitHandle[waitableResults.Length + 1];
                GetWaitHandles(waitableResults, waitHandles);
                waitHandles[waitableResults.Length] = cancelWaitHandle;
            }
            else
            {
                waitHandles = new WaitHandle[waitableResults.Length];
                GetWaitHandles(waitableResults, waitHandles);
            }

            int result = STPEventWaitHandle.WaitAny(waitHandles, millisecondsTimeout, exitContext);

            // Treat cancel as timeout
            if (cancelWaitHandle is not null)
            {
                if (result == waitableResults.Length)
                {
                    result = STPEventWaitHandle.WaitTimeout;
                }
            }

            ReleaseWaitHandles(waitableResults);

            return result;
        }

        /// <summary>
        /// Fill an array of wait handles with the work items wait handles.
        /// </summary>
        /// <param name="waitableResults">An array of work item results</param>
        /// <param name="waitHandles">An array of wait handles to fill</param>
        private static void GetWaitHandles(IWaitableResult[] waitableResults,
            WaitHandle[] waitHandles)
        {
            for (int i = 0; i < waitableResults.Length; ++i)
            {
                WorkItemResult wir = waitableResults[i].GetWorkItemResult() as WorkItemResult;
                Debug.Assert(wir is not null, "All waitableResults must be WorkItemResult objects");

                waitHandles[i] = wir.GetWorkItem().GetWaitHandle();
            }
        }

        /// <summary>
        /// Release the work items' wait handles
        /// </summary>
        /// <param name="waitableResults">An array of work item results</param>
        private static void ReleaseWaitHandles(IWaitableResult[] waitableResults)
        {
            for (int i = 0; i < waitableResults.Length; ++i)
            {
                WorkItemResult wir = (WorkItemResult)waitableResults[i].GetWorkItemResult();

                wir.GetWorkItem().ReleaseWaitHandle();
            }
        }

        #endregion

        #region Private Members

        private WorkItemState GetWorkItemState()
        {
            lock (this)
            {
                if (WorkItemState.Completed == m_workItemState)
                {
                    return m_workItemState;
                }
                if (WorkItemState.Canceled != m_workItemState && DateTime.UtcNow.Ticks > m_expirationTime)
                {
                    m_workItemState = WorkItemState.Canceled;
                    return m_workItemState;
                }
                if(WorkItemState.InProgress != m_workItemState)
                {
                    if (CanceledSmartThreadPool.IsCanceled || CanceledWorkItemsGroup.IsCanceled)
                    {
                        return WorkItemState.Canceled;
                    }
                }
                return m_workItemState;
            }
        }


        /// <summary>
        /// Sets the work item's state
        /// </summary>
        /// <param name="workItemState">The state to set the work item to</param>
        private void SetWorkItemState(WorkItemState workItemState)
        {
            lock (this)
            {
                if (IsValidStatesTransition(m_workItemState, workItemState))
                {
                    m_workItemState = workItemState;
                }
            }
        }

        /// <summary>
        /// Signals that work item has been completed or canceled
        /// </summary>
        /// <param name="canceled">Indicates that the work item has been canceled</param>
        private void SignalComplete(bool canceled)
        {
            SetWorkItemState(canceled ? WorkItemState.Canceled : WorkItemState.Completed);
            lock (this)
            {
                // If someone is waiting then signal.
                m_workItemCompleted?.Set();
            }
        }

        internal void WorkItemIsQueued()
        {
            _waitingOnQueueStopwatch.Start();
        }

        #endregion

        #region Members exposed by WorkItemResult

        /// <summary>
        /// Cancel the work item if it didn't start running yet.
        /// </summary>
        /// <returns>Returns true on success or false if the work item is in progress or already completed</returns>
        private bool Cancel(bool abortExecution)
        {
            bool success = false;
            bool signalComplete = false;

            lock (this)
            {
                switch (GetWorkItemState())
                {
                    case WorkItemState.Canceled:
                        //Debug.WriteLine("Work item already canceled");
                        if (abortExecution)
                        {
                            Thread executionThread = Interlocked.CompareExchange(ref m_executingThread, null, m_executingThread);
                            if (executionThread is not null)
                            {
                                //executionThread.Abort(); // "Cancel"
                                // No need to signalComplete, because we already cancelled this work item
                                // so it already signaled its completion.
                                //signalComplete = true;
                            }
                        }
                        success = true;
                        break;
                    case WorkItemState.Completed:
                        //Debug.WriteLine("Work item cannot be canceled");
                        break;
                    case WorkItemState.InProgress:
                        if (abortExecution)
                        {
                            Thread executionThread = Interlocked.CompareExchange(ref m_executingThread, null, m_executingThread);
                            if (executionThread is not null)
                            {
                                //executionThread.Abort(); // "Cancel"
                                success = true;
                                signalComplete = true;
                            }
                        }
                        else
                        {
                            // **************************
                            // Stock SmartThreadPool 2.2.3 sets these to true and relies on the thread to check the
                            // WorkItem cancellation status.  However, OpenSimulator uses a different mechanism to notify
                            // scripts of co-operative termination and the abort code also relies on this method
                            // returning false in order to implement a small wait.
                            //
                            // Therefore, as was the case previously with STP, we will not signal successful cancellation
                            // here.  It's possible that OpenSimulator code could be changed in the future to remove
                            // the need for this change.
                            // **************************
                            success = false;
                            signalComplete = false;
                        }
                        break;
                    case WorkItemState.InQueue:
                        // Signal to the wait for completion that the work
                        // item has been completed (canceled). There is no
                        // reason to wait for it to get out of the queue
                        signalComplete = true;
                        //Debug.WriteLine("Work item canceled");
                        success = true;
                        break;
                }

                if (signalComplete)
                {
                    SignalComplete(true);
                }
            }
            return success;
        }

        /// <summary>
        /// Get the result of the work item.
        /// If the work item didn't run yet then the caller waits for the result, timeout, or cancel.
        /// In case of error the method throws and exception
        /// </summary>
        /// <returns>The result of the work item</returns>
        private object GetResult(int millisecondsTimeout, bool exitContext,
            WaitHandle cancelWaitHandle)
        {
            object result = GetResult(millisecondsTimeout, exitContext, cancelWaitHandle, out Exception e);
            if (e is not null)
            {
                throw new WorkItemResultException("The work item caused an excpetion, see the inner exception for details", e);
            }
            return result;
        }

        /// <summary>
        /// Get the result of the work item.
        /// If the work item didn't run yet then the caller waits for the result, timeout, or cancel.
        /// In case of error the e argument is filled with the exception
        /// </summary>
        /// <returns>The result of the work item</returns>
        private object GetResult( int millisecondsTimeout, bool exitContext,
            WaitHandle cancelWaitHandle, out Exception e)
        {
            e = null;

            // Check for cancel
            if (WorkItemState.Canceled == GetWorkItemState())
            {
                throw new WorkItemCancelException("Work item canceled");
            }

            // Check for completion
            if (IsCompleted)
            {
                e = m_exception;
                return m_result;
            }

            // If no cancelWaitHandle is provided
            if (cancelWaitHandle is null)
            {
                WaitHandle wh = GetWaitHandle();

                bool timeout = !STPEventWaitHandle.WaitOne(wh, millisecondsTimeout, exitContext);

                ReleaseWaitHandle();

                if (timeout)
                {
                    throw new WorkItemTimeoutException("Work item timeout");
                }
            }
            else
            {
                WaitHandle wh = GetWaitHandle();
                int result = STPEventWaitHandle.WaitAny(new WaitHandle[] { wh, cancelWaitHandle });
                ReleaseWaitHandle();

                switch (result)
                {
                    case 0:
                        // The work item signaled
                        // Note that the signal could be also as a result of canceling the 
                        // work item (not the get result)
                        break;
                    case 1:
                    case STPEventWaitHandle.WaitTimeout:
                        throw new WorkItemTimeoutException("Work item timeout");
                    default:
                        Debug.Assert(false);
                        break;

                }
            }

            // Check for cancel
            if (WorkItemState.Canceled == GetWorkItemState())
            {
                throw new WorkItemCancelException("Work item canceled");
            }

            Debug.Assert(IsCompleted);

            e = m_exception;

            // Return the result
            return m_result;
        }

        /// <summary>
        /// A wait handle to wait for completion, cancel, or timeout 
        /// </summary>
        private WaitHandle GetWaitHandle()
        {
            lock (this)
            {
                if (m_workItemCompleted is null)
                {
                    m_workItemCompleted = new ManualResetEvent(IsCompleted);
                }
                ++m_workItemCompletedRefCount;
            }
            return m_workItemCompleted;
        }

        private void ReleaseWaitHandle()
        {
            lock (this)
            {
                if (m_workItemCompleted is not null)
                {
                    --m_workItemCompletedRefCount;
                    if (0 == m_workItemCompletedRefCount)
                    {
                        m_workItemCompleted.Close();
                        m_workItemCompleted = null;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true when the work item has completed or canceled
        /// </summary>
        private bool IsCompleted
        {
            get
            {
                lock (this)
                {
                    WorkItemState workItemState = GetWorkItemState();
                    return ((workItemState == WorkItemState.Completed) ||
                            (workItemState == WorkItemState.Canceled));
                }
            }
        }

        /// <summary>
        /// Returns true when the work item has canceled
        /// </summary>
        public bool IsCanceled
        {
            get
            {
                lock (this)
                {
                    return (GetWorkItemState() == WorkItemState.Canceled);
                }
            }
        }

        #endregion
 
        internal event WorkItemStateCallback OnWorkItemStarted
        {
            add
            {
                m_workItemStartedEvent += value;
            }
            remove
            {
                m_workItemStartedEvent -= value;
            }
        }

        internal event WorkItemStateCallback OnWorkItemCompleted
        {
            add
            {
                m_workItemCompletedEvent += value;
            }
            remove
            {
                m_workItemCompletedEvent -= value;
            }
        }

        public void DisposeOfState()
        {
            if(m_callerContext is not null)
            {
                m_callerContext.Dispose();
                m_callerContext = null;
            }

            if(m_workItemCompleted is not null)
            {
                m_workItemCompleted.Dispose();
                m_workItemCompleted = null;
            }

            if (m_workItemInfo.DisposeOfStateObjects)
            {
                if (m_state is IDisposable disp)
                {
                    disp.Dispose();
                    m_state = null;
                }
            }
            m_callback = null;
            m_callbackNoResult = null;
        }
    }
}
