﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;

namespace NetChan {
﻿
    /// <summary>
    /// Asynchronous channel for communicating between threads, CSP-like semantics but with a fixed size queue
    /// </summary>
    public class QueuedChannel<T> : IChannel<T> {
        private readonly object sync = new object();
        private readonly Queue<Waiter<T>> recq = new Queue<Waiter<T>>();
        private readonly Queue<Waiter<T>> sendq = new Queue<Waiter<T>>();
        private readonly Queue<T> itemq;
        private readonly int capacity;
        private bool closed;

        public int Capacity {
            get { return capacity; }
        }

        public QueuedChannel(int capacity) {
            if (capacity < 0) {
                throw new ArgumentOutOfRangeException("capacity", capacity, "must be zero or more");
            }
            this.capacity = capacity;
            itemq = new Queue<T>(capacity);
        }

        /// <summary>
        /// Marks a Channel as closed, preventing further Send operations often
        /// After calling Close, and after any previously sent values have been received, 
        /// receive operations will return the default value for Channel's type without blocking
        /// </summary>
        public void Close() {
            lock (sync) {
                if (closed) {
                    return;
                }
                closed = true;
                if (sendq.Count == 0 && itemq.Count == 0 && recq.Count > 0) {
                    // wait up the waiting recievers
                    Debug.Print("Thread {0}, {1} Close is waking {2} waiting receivers", Thread.CurrentThread.ManagedThreadId, GetType(), recq.Count);
                    foreach (var r in recq) {
                        r.Wakeup();
                    }
                }
                Debug.Print("Thread {0}, {1} is now Closed", Thread.CurrentThread.ManagedThreadId, GetType());
            }
        }

        /// <summary>
        /// Send a value, adds it to the item queue or blocks until the queue is no longer full
        /// </summary>
        public void Send(T v) {
            Waiter<T> s = null;
            lock (sync) {
                if (closed) {
                    throw new ClosedChannelException("You cannot send on a closed Channel");
                }
                if (itemq.Count == capacity) {
                    // at capacity, queue our waiter until some capacity is freed up by a recv
                    s = WaiterPool<T>.Get(v);
                    sendq.Enqueue(s);
                } else {
                    // spare capacity
                    Debug.Print("Thread {0}, {1} Send({2}), spare capacity, adding to itemq", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                    itemq.Enqueue(v);
                }
                // loop to see if there is a waiting reciever
                var val = itemq.Peek();
                while (recq.Count > 0) {
                    Waiter<T> r = recq.Dequeue();
                    if (r.SetItem(val)) {
                        Debug.Print("Thread {0}, {1} Send({2}), SetItem suceeded", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                        itemq.Dequeue();    // really remove from queue
                        r.Wakeup();
                        break; // SetItem might fail if a select has fired
                    } else {
                        Debug.Print("Thread {0}, {1} Send({2}), SetItem failed due to select", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                    }
                }
            }
            if (s != null) {
                // wait for the reciever to wake us up
                Debug.Print("Thread {0}, {1} Send({2}), waiting ", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                s.WaitOne();
                Debug.Print("Thread {0}, {1} Send({2}), woke up after waiting ", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                WaiterPool<T>.Put(s);
            }
        }

        public bool TrySend(T v) {
            lock (sync) {
                if (closed) {
                    // you cannot send on a closed channel
                    return false;
                }
                if (itemq.Count == capacity) {
                    // queue is full and we don't want to wait
                    return false;
                }
                itemq.Enqueue(v);
                // loop to see if there is a waiting reciever
                while (recq.Count > 0) {
                    Waiter<T> r = recq.Dequeue();
                    if (r.SetItem(v)) {
                        r.Wakeup();
                        break;
                    }
                }
            }
            return true;
        }

        public T Recv() {
            var maybe = BlockingRecv();
            return maybe.Value;
        }

        /// <summary>
        /// Returns an item, blocking if non ready, until a Channel is closed
        /// </summary>
        public Maybe<T> BlockingRecv() {
            Waiter<T> r;
            lock (sync) {
                MoveSendQToItemQ();
                if (itemq.Count > 0) {
                    var value = itemq.Dequeue();
                    Debug.Print("Thread {0}, {1} BlockingRecv, removed item from itemq", Thread.CurrentThread.ManagedThreadId, GetType());
                    MoveSendQToItemQ();
                    return Maybe<T>.Some(value);
                } else {
                    Debug.Print("Thread {0}, {1} BlockingRecv, itemq is empty", Thread.CurrentThread.ManagedThreadId, GetType());
                }
                if (closed) {
                    Debug.Print("Thread {0}, {1} BlockingRecv, Channel is closed", Thread.CurrentThread.ManagedThreadId, GetType());
                    return Maybe<T>.None("closed");
                }
                r = WaiterPool<T>.Get();
                recq.Enqueue(r);
            }
            // wait for a sender to signal it has sent
            Debug.Print("Thread {0}, {1} BlockingRecv, waiting", Thread.CurrentThread.ManagedThreadId, GetType());
            r.WaitOne();
            Debug.Print("Thread {0}, {1} BlockingRecv, woke up", Thread.CurrentThread.ManagedThreadId, GetType());
            var t = r.Item;
            WaiterPool<T>.Put(r);
            return t;
        }

        private void MoveSendQToItemQ() {
            if (itemq.Count < capacity && sendq.Count > 0) {
                Waiter<T> s = sendq.Dequeue();
                itemq.Enqueue(s.Item.Value);
                Debug.Print("Thread {0}, {1} BlockingRecv, waking sender", Thread.CurrentThread.ManagedThreadId, GetType());
                s.Wakeup();
            }
        }

        public Maybe<T> TryRecv() {
            lock (sync) {
                MoveSendQToItemQ();
                if (itemq.Count > 0) {
                    var value = itemq.Dequeue();
                    MoveSendQToItemQ();
                    return Maybe<T>.Some(value);
                }
                return Maybe<T>.None(closed ? "closed" : "No senders");
            }
        }

        public IEnumerator<T> GetEnumerator() {
            for (; ; ) {
                var maybe = BlockingRecv();
                if (maybe.Absent) {
                    yield break; // Channel has been closed
                }
                yield return maybe.Value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }

        public void Dispose() {
            Close();
        }

        /// <summary>
        /// Try to receive and value and write it to h, if it can be recieved straight away then it returns <see cref="RecvStatus.Read"/>.
        /// If the channel is closed it returns <see cref="RecvStatus.Closed"/>.
        /// If it has to wait it registers a reciever and returns <see cref="RecvStatus.Waiting"/>.
        /// </summary>
        /// <param name="sync">a object used to ensure only one channel writes a value as part of the select</param>
        RecvStatus IUntypedReceiver.RecvSelect(Sync selSync, out IWaiter outr) {
            var r = WaiterPool<T>.Get();
            outr = r;
            lock (sync) {
                MoveSendQToItemQ();
                while (itemq.Count > 0) {
                    r.Sync = selSync;
                    if (r.SetItem(itemq.Peek())) {
                        itemq.Dequeue();
                        Debug.Print("Thread {0}, {1} RecvSelect, removed {2} from itemq", Thread.CurrentThread.ManagedThreadId, GetType(), r.Item);
                        MoveSendQToItemQ();
                        return RecvStatus.Read;
                    }
                }
                if (closed) {
                    return RecvStatus.Closed;
                }
                r.Sync = selSync;
                recq.Enqueue(r);
            }
            return RecvStatus.Waiting;
        }

        /// <summary>Try to receive without blocking</summary>
        Maybe<object> IUntypedReceiver.TryRecvSelect() {
            lock (sync) {
                MoveSendQToItemQ();
                if (itemq.Count == 0) {
                    Debug.Print("Thread {0}, {1} TryRecvSelect, itemq is empty", Thread.CurrentThread.ManagedThreadId, GetType());
                    return Maybe<object>.None();
                }
                var v = itemq.Dequeue();
                Debug.Print("Thread {0}, {1} TryRecvSelect, removed {2} from itemq", Thread.CurrentThread.ManagedThreadId, GetType(), v);
                MoveSendQToItemQ();
                return Maybe<object>.Some(v);
            }
        }

        void IUntypedReceiver.Release(IWaiter h) {
            WaiterPool<T>.Put((Waiter<T>)h);
        }
        
    }

}
