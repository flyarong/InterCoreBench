using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace InterCoreBench
{
    class Program
    {
        const int TestPeriodInMs = 5000;
        const int TestIntervalInMs = 1000;
        const int TestCopyBlockSize = 128 * 1024;
        const bool EnableReverseBandwidthTest = false; // Maybe useful in HMP systems

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        static volatile bool cancel = false;

        static (ulong count, TimeSpan elapsed) DoSync(int core, SemaphoreSlim wait, SemaphoreSlim notify, bool doCount)
        {
            try
            {
                var mask = 1L << core;
                var process = Process.GetCurrentProcess();
                var threadId = GetCurrentThreadId();
                bool set = false;
                foreach (ProcessThread thread in process.Threads)
                {
                    if (thread.Id == threadId)
                    {
                        thread.ProcessorAffinity = (IntPtr)mask;
                        set = true;
                    }
                }

                if (!set)
                {
                    throw new Exception("Failed to set affinity");
                }

                Thread.BeginThreadAffinity();
                var sw = new Stopwatch();
                sw.Start();
                
                var sc = 0UL;
                while (!cancel)
                {
                    wait.Wait();
                    sc++;
                    notify.Release();
                }

                sw.Stop();
                Thread.EndThreadAffinity();
                return (sc, sw.Elapsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return default;
            }
        }

        static (ulong count, TimeSpan elapsed) DoCopy(int core, byte[] from, byte[] to, SemaphoreSlim wait, SemaphoreSlim notify, bool doCount)
        {
            try
            {
                var mask = 1L << core;
                var process = Process.GetCurrentProcess();
                var threadId = GetCurrentThreadId();
                bool set = false;
                foreach (ProcessThread thread in process.Threads)
                {
                    if (thread.Id == threadId)
                    {
                        thread.ProcessorAffinity = (IntPtr)mask;
                        set = true;
                    }
                }

                if (!set)
                {
                    throw new Exception("Failed to set affinity");
                }

                Thread.BeginThreadAffinity();
                var sw = new Stopwatch();
                sw.Start();
                
                var sc = 0UL;
                while (!cancel)
                {
                    wait.Wait();
                    Buffer.BlockCopy(from, 0, to, 0, from.Length);
                    sc++;
                    notify.Release();
                }

                sw.Stop();
                Thread.EndThreadAffinity();
                return (sc, sw.Elapsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return default;
            }
        }

        static void TestSync(int c1, int c2)
        {
            using (var s1 = new SemaphoreSlim(1))
            using (var s2 = new SemaphoreSlim(0))
            {
                cancel = false;
                Console.Write($"Testing latency between logical core {c1} and {c2}... ");
                var t1 = Task.Run(() => DoSync(c1, s1, s2, true));
                var t2 = Task.Run(() => DoSync(c2, s2, s1, false));
                Thread.Sleep(TestPeriodInMs);
                cancel = true;
                var (count, elapsed) = t1.GetAwaiter().GetResult();
                t2.GetAwaiter().GetResult();

                Console.WriteLine($"{elapsed.TotalMilliseconds * 1000 * 1000 / count / 2:0} ns ({count} synchronizations in {elapsed.TotalMilliseconds:0} ms)");
            }
        }

        static unsafe void TestCopy(int c1, int c2)
        {
            using (var s1 = new SemaphoreSlim(1))
            using (var s2 = new SemaphoreSlim(0))
            {
                cancel = false;
                Console.Write($"Testing bandwidth between logical core {c1} and {c2}... ");
                byte[] c1s = new byte[TestCopyBlockSize], s = new byte[TestCopyBlockSize], c2d = new byte[TestCopyBlockSize];
                (new Random()).NextBytes(c1s);
                var t1 = Task.Run(() => DoCopy(c1, c1s, s, s1, s2, true));
                var t2 = Task.Run(() => DoCopy(c2, s, c2d, s2, s1, false));
                Thread.Sleep(TestPeriodInMs);
                cancel = true;
                var (count, elapsed) = t1.GetAwaiter().GetResult();
                t2.GetAwaiter().GetResult();

                Console.WriteLine($"{(double)count * TestCopyBlockSize / 1024 / 1024 / 1024 / elapsed.TotalSeconds:0.00} GB/s ({(double)count * TestCopyBlockSize / 1024 / 1024 / 1024:0.00} GB copied in {elapsed.TotalMilliseconds:0} ms)");
            }
        }

        static void Main(string[] args)
        {
            var coreRelationInfo = LogicalCoreInfo.GetLogicalProcessorInformation()
                .Where(i => i.Relationship == LogicalCoreInfo.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                .ToList();
            var physicalCores = new List<int>();
            foreach (var info in coreRelationInfo)
            {
                var coreIndex = (int)Math.Log2((long)info.ProcessorMask & -(long)info.ProcessorMask);
                physicalCores.Add(coreIndex);
            }

            for (var i = 0; i < physicalCores.Count - 1; i++)
            {
                for (var j = i + 1; j < physicalCores.Count; j++)
                {
                    TestSync(physicalCores[i], physicalCores[j]);
                    Thread.Sleep(TestIntervalInMs);
                    TestCopy(physicalCores[i], physicalCores[j]);
                    Thread.Sleep(TestIntervalInMs);
                    if (EnableReverseBandwidthTest)
                    {
                        TestCopy(physicalCores[j], physicalCores[i]);
                        Thread.Sleep(TestIntervalInMs);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("Test finished, press any key to exit.");
            Console.ReadKey();
        }
    }
}
