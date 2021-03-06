﻿/***

   Copyright (C) 2018-2020. dc-koromo. All Rights Reserved.
   
   Author: Koromo Copy Developer

***/

using Koromo_Copy.Interface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Koromo_Copy.Net
{
    public class DownloadQueue : ISemaphore
    {
        public int capacity = 32;
        public int mtx = 0;
        public List<Tuple<string, string, object, SemaphoreCallBack, SemaphoreExtends>> queue = new List<Tuple<string, string, object, SemaphoreCallBack, SemaphoreExtends>>();
        public List<Tuple<string, HttpWebRequest>> requests = new List<Tuple<string, HttpWebRequest>>();
        public List<string> aborted = new List<string>();
        public List<Task> tasks = new List<Task>();
        public IWebProxy proxy { get; set; }

        public delegate void DownloadSizeCallBack(string uri, long size, object obj);
        public delegate void DownloadStatusCallBack(string uri, int size, object obj);
        public delegate void RetryCallBack(string uri, object obj);

        DownloadSizeCallBack download_callback;
        DownloadStatusCallBack status_callback;
        RetryCallBack retry_callback;
        
        object notify_lock = new object();
        object shutdown_lock = new object();
        object task_lock = new object();
        volatile bool preempt_take = false;
        
        /// <summary>
        /// 다운로드 큐의 생성자 입니다.
        /// </summary>
        /// <param name="notify_size">다운로드가 시작되기전 파일의 크기를 알릴때 호출됩니다.</param>
        /// <param name="notify_status">다운로드 중인 파일의 </param>
        /// <param name="retry"></param>
        public DownloadQueue(DownloadSizeCallBack notify_size, DownloadStatusCallBack notify_status, RetryCallBack retry)
        {
            capacity = Settings.Instance.Model.Thread;
            ServicePointManager.DefaultConnectionLimit = Settings.Instance.Net.ServicePointConnectionLimit;
            timeout_infinite = Settings.Instance.Net.TimeoutInfinite;
            timeout_ms = Settings.Instance.Net.TimeoutMillisecond;
            buffer_size = Settings.Instance.Net.DownloadBufferSize;
            download_callback = notify_size;
            status_callback = notify_status;
            retry_callback = retry;
            proxy = null;
        }
        
        /// <summary>
        /// Request 타임아웃 관련 설정입니다.
        /// </summary>
        public bool timeout_infinite = false;
        public int timeout_ms = 10000;
        public int buffer_size = 131072;

        /// <summary>
        /// 이 플래그가 true면 모든 다운로드과정을 정지합니다.
        /// </summary>
        public bool shutdown = false;

        /// <summary>
        /// 세마포어가 허용하는 총 스레드 수를 가져옵니다.
        /// </summary>
        public int Capacity { get { return capacity; } set { capacity = value; } }

        /// <summary>
        /// 특정 작업을 취소합니다.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public bool Abort(string url)
        {
            lock (queue)
            {
                for (int i = 0; i < queue.Count; i++)
                    if (queue[i].Item1 == url)
                    {
                        queue.RemoveAt(i);
                        Interlocked.Decrement(ref mtx);
                        lock (notify_lock) Notify();
                        break;
                    }
            }
            lock (requests)
            {
                foreach (var i in requests)
                    if (i.Item1 == url)
                        lock (i.Item2) i.Item2.Abort();
            }
            aborted.Add(url);
            return false;
        }

        /// <summary>
        /// 모든 작업을 취소하고, 다운로드 중인 파일을 삭제합니다.
        /// </summary>
        public void Abort()
        {
            lock (requests)
            {
                lock (shutdown_lock) shutdown = true;

                lock (queue)
                {
                    foreach (var vp in queue) try { File.Delete(vp.Item2); } catch { }
                    queue.Clear();
                }
                for (int i = requests.Count - 1; i >= 0; i--)
                    requests[i].Item2.Abort();
            }
        }

        /// <summary>
        /// 새로운 작업을 큐에 추가합니다.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="path"></param>
        /// <param name="obj"></param>
        /// <param name="callback"></param>
        /// <param name="se"></param>
        public void Add(string url, string path, object obj, SemaphoreCallBack callback, SemaphoreExtends se = null)
        {
            queue.Add(new Tuple<string, string, object, SemaphoreCallBack, SemaphoreExtends>(url, path, obj, callback, se));
            if (Wait())
                Notify();
        }

        /// <summary>
        /// 큐를 일시정지합니다.
        /// </summary>
        public void Preempt()
        {
            preempt_take = true;
        }

        /// <summary>
        /// 큐를 재활성화합니다.
        /// </summary>
        public void Reactivation()
        {
            preempt_take = false;
        }
        
        private bool Wait()
        {
            if (mtx == capacity)
                return false;
            return true;
        }

        private void Notify()
        {
            int i = mtx;
            if (queue.Count > i)
            {
                string s1 = queue[i].Item1;
                string s2 = queue[i].Item2;
                object s3 = queue[i].Item3;
                SemaphoreCallBack s4 = queue[i].Item4;
                SemaphoreExtends s5 = queue[i].Item5;
                lock(task_lock)
                tasks.Add(Task.Run(() => DownloadRemoteImageFile(s1, s2, s3, s4, s5)).ContinueWith(
                    x => Task.Run(() => { lock (task_lock) tasks.RemoveAll(y => y.IsCompleted); })));
                Interlocked.Increment(ref mtx);
            }
        }

        private void DownloadRemoteImageFile(string uri, string fileName, object obj, SemaphoreCallBack callback, SemaphoreExtends se)
        {
            int retry_count = 0;
        RETRY:
            if (retry_count > Settings.Instance.Net.RetryCount)
            {
                Monitor.Instance.Push($"[Many Retry] {uri} is auto deleted in download queue.");
                return;
            }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            se.RunPass(ref request);

            request.Timeout = timeout_infinite ? Timeout.Infinite : timeout_ms;
            request.KeepAlive = true;
            request.Proxy = proxy;

            lock (requests) requests.Add(new Tuple<string, HttpWebRequest>(uri, request));

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        Monitor.Instance.Push($"404 Not Found {uri}");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK ||
                        response.StatusCode == HttpStatusCode.Moved ||
                        response.StatusCode == HttpStatusCode.Redirect)
                    {
                        using (Stream inputStream = response.GetResponseStream())
                        using (Stream outputStream = File.OpenWrite(fileName))
                        {
                            byte[] buffer = new byte[buffer_size];
                            int bytesRead;
                            lock (download_callback) download_callback(uri, response.ContentLength, obj);
                            do
                            {
                                bytesRead = inputStream.Read(buffer, 0, buffer.Length);
                                outputStream.Write(buffer, 0, bytesRead);
                                lock (status_callback) status_callback(uri, bytesRead, obj);
                                lock (shutdown_lock) if (shutdown) break;
                                if (preempt_take)
                                {
                                    Monitor.Instance.Push($"[Preempt Queue] {uri}");
                                    while (preempt_take)
                                        Thread.Sleep(500);
                                    Monitor.Instance.Push($"[Exit Preempt] {uri}");
                                }
                            } while (bytesRead != 0);
                        }
                        lock (shutdown_lock) if (shutdown)
                            {
                                File.Delete(fileName);
                                Monitor.Instance.Push($"[Shutdown] {uri}");
                                return;
                            }
                    }
                }
            }
            catch (Exception e)
            {
                Monitor.Instance.Push($"[{retry_count}] {e.Message}");
                lock (aborted)
                    if (!aborted.Contains(uri))
                    {
                        lock (retry_callback) retry_callback(uri, obj);
                        request.Abort();
                        Thread.Sleep(1000);
                        retry_count++;
                        goto RETRY;
                    }
                    else
                    {
                        File.Delete(fileName);
                        lock (callback) callback(uri, fileName, obj);
                        return;
                    }
            }

            lock (callback) callback(uri, fileName, obj);

            lock (queue)
            {
                int at = 0;
                for (; at < queue.Count; at++)
                    if (queue[at].Item1 == uri && queue[at].Item2 == fileName)
                        break;
                if (at != queue.Count) queue.RemoveAt(at);
            }

            Interlocked.Decrement(ref mtx);
            lock (notify_lock) Notify();
        }
    }
}
