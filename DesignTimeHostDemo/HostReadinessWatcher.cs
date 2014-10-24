// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;

namespace DesignTimeHostDemo.Test
{
    /// <summary>
    /// Used to determine if all conditions are met for a test to run.
    /// 
    /// Strategy: if the it has been more than a specific period time since last pinging
    /// it is assumed the host enters idle status.
    /// </summary>
    public class HostReadinessWatcher
    {
        private DateTime        _lastPing;
        private AutoResetEvent  _waitHandler;
        private readonly int    _silentThreshold;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="silentThresold">
        /// Threshold of time to determine if the host enters idle.
        /// In milliseconds. Default value 1000 ms.
        /// </param>
        public HostReadinessWatcher(int silentThresold = 1000)
        {
            _waitHandler = new AutoResetEvent(false);
            _silentThreshold = silentThresold;
            _lastPing = DateTime.MaxValue;
        }

        /// <summary>
        /// Start the watcher
        /// </summary>
        public void Start()
        {
            new Thread(() =>
            {
                while (true)
                {
                    TimeSpan sinceLastPing;
                    lock (_waitHandler)
                    {
                        sinceLastPing = DateTime.Now - _lastPing;
                    }

                    if (sinceLastPing > TimeSpan.FromMilliseconds(_silentThreshold))
                    {
                        Console.WriteLine("It has been {0:F2} ms since last ping. The host enters idle.", sinceLastPing.TotalMilliseconds);
                        break;
                    }
                }

                _waitHandler.Set();
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Blocks the current thread, wait for the host to enter idle.
        /// </summary>
        public void Wait()
        {
            _waitHandler.WaitOne();
        }

        /// <summary>
        /// Ping the wather to indicate the host is still running. Usually happens when messages are
        /// recived or output is printed.
        /// </summary>
        public void Ping()
        {
            lock (_waitHandler)
            {
                _lastPing = DateTime.Now;
                Console.WriteLine("Ping at " + _lastPing.ToString("mm:ss.ffff"));
            }
        }
    }
}
