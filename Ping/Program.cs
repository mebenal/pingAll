using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace PingProg
{
    class Program
    {
        static public long _addrPinged = 0;
        static public long _addrSuccess = 0;
        static Stopwatch _elapsed = new();

        static async Task<int> PingRange(EnumerateIPAddress addresses)
        {
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = 1000
            };
            _elapsed.Start();
            await Parallel.ForEachAsync(addresses, parallelOptions, async (addr, ctr) => await AsyncPing2(addr, 1500));
            Console.WriteLine(_addrPinged);
            return 0;
        }

        static List<Task> PingRange2(EnumerateIPAddress addresses)
        {
            TransformBlock<EnumerateIPAddress, List<Task>> test = new(addresses =>
            {
                List<Task> test = new();
                foreach (IPAddress addr in addresses)
                {
                    test.Add(AsyncPing(addr, 3000));
                }
                return test;
            });

            test.Post(addresses);
            test.Complete();
            return test.Receive();
        }

        public static async Task AwaitCancelRetry(Func<CancellationToken, Task> function, int timeout, int maxAttempts, CancellationToken externalToken = default)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, default);
                cts.CancelAfter(timeout);
                try
                {
                    await function(cts.Token); // Continue on captured context
                }
                catch (OperationCanceledException ex)
                    when (ex.CancellationToken == cts.Token && !externalToken.IsCancellationRequested)
                {
                    continue;
                }
            }
        }

        public static async Task AsyncPing2(IPAddress addr, int timeout)
        {
            AutoResetEvent waiter = new AutoResetEvent(false);

            Ping pingSender = new Ping();

            // When the PingCompleted event is raised,
            // the PingCompletedCallback method is called.
            pingSender.PingCompleted += new PingCompletedEventHandler(PingCompletedCallback);

            // Create a buffer of 32 bytes of data to be transmitted.
            //string data = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            byte[] buffer = new byte[64];
            

            // Set options for transmission:
            // The data can go through 64 gateways or routers
            // before it is destroyed, and the data packet
            // cannot be fragmented.
            PingOptions options = new PingOptions(64, true);

            //Console.WriteLine("Time to live: {0}", options.Ttl);
            //Console.WriteLine("Don't fragment: {0}", options.DontFragment);

            // Send the ping asynchronously.
            // Use the waiter as the user token.
            // When the callback completes, it can wake up this thread.
            pingSender.SendAsync(addr, timeout, buffer, options, waiter);
            await Task.Delay(timeout);
            pingSender.SendAsyncCancel();
            Interlocked.Increment(ref _addrPinged);
        }

        private static void PingCompletedCallback(object sender, PingCompletedEventArgs e)
        {
            // If the operation was canceled, display a message to the user.
            if (e.Cancelled)
            {
                ///Console.WriteLine("Ping canceled.");

                // Let the main thread resume.
                // UserToken is the AutoResetEvent object that the main thread
                // is waiting for.
                ((AutoResetEvent)e.UserState).Set();
            }

            // If an error occurred, display the exception to the user.
            if (e.Error != null)
            {
                //Console.WriteLine("Ping failed:");
                //Console.WriteLine(e.Error.ToString());

                // Let the main thread resume.
                ((AutoResetEvent)e.UserState).Set();
            }

            PingReply reply = e.Reply;

            DisplayReply(reply);

            // Let the main thread resume.
            ((AutoResetEvent)e.UserState).Set();
        }

        public static void DisplayReply(PingReply reply)
        {
            if (reply == null)
                return;

            //Console.WriteLine("ping status: {0}", reply.Status);
            if (reply.Status == IPStatus.Success)
            {
                Interlocked.Increment(ref _addrSuccess);
                //Console.WriteLine("Address: {0}", reply.Address.ToString());
                //Console.WriteLine("RoundTrip time: {0}", reply.RoundtripTime);
                //Console.WriteLine("Time to live: {0}", reply.Options.Ttl);
                //Console.WriteLine("Don't fragment: {0}", reply.Options.DontFragment);
                //Console.WriteLine("Buffer size: {0}", reply.Buffer.Length);
            }
        }

        public static async Task AsyncPing(IPAddress addr, int timeoutPing)
        {
            Ping ping = new Ping();
            PingReply reply = await ping.SendPingAsync(addr, timeoutPing);
            if (reply.Status == IPStatus.Success)
            {
                Interlocked.Increment(ref _addrSuccess);
            }
            Interlocked.Increment(ref _addrPinged);
            return;

        }
        
        static long ToInt(IPAddress addr)
        {
            // careful of sign extension: convert to uint first;
            // unsigned NetworkToHostOrder ought to be provided.
            byte[] bytes = addr.GetAddressBytes();

            // flip big-endian(network order) to little-endian
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return (long)BitConverter.ToUInt32(bytes, 0);
        }

        public static async Task GetResponse(IEnumerable<IPAddress> IPAddresses)
        {
            await Parallel.ForEachAsync(IPAddresses, async (addr, cts) =>
            {
                Ping ping = new Ping();
                PingReply reply = await ping.SendPingAsync(addr, 500);
                if (ToInt(addr) % 65536 == 0)
                {
                    Console.WriteLine(string.Format("{0,-17}{1,-8}{2,-12}{3}", $"{addr.ToString()}:", _elapsed.ElapsedMilliseconds / 1000.0, _addrPinged, _addrSuccess));
                    _elapsed.Restart();
                }
                if (reply.Status == IPStatus.Success)
                {
                    Console.WriteLine(addr.ToString());
                    Interlocked.Increment(ref _addrSuccess);
                }
                Interlocked.Increment(ref _addrPinged);
            });

        }

        public static IPRange SetIPRange(
            int startI = 1, int endI = 255,
            int startJ = 0, int endJ = 255,
            int startK = 0, int endK = 255,
            int startL = 0, int endL = 255)
        {
            IPRange range = new();
            range.StartI = startI;
            range.EndI = endI;
            range.StartJ = startJ;
            range.EndJ = endJ;
            range.StartK = startK;
            range.EndK = endK;
            range.StartL = startL;
            range.EndL = endL;
            return range;
        }

        private async static Task Main(string[] args)
        {
            int numInstances = 12;
            int octetsPerInstance = 1;
            _elapsed.Start();
            for (int i = 1; i <= 255; i++)
            {
                for (int j = 0; j <= 255; j++)
                {
                    for (int k = 0; k <= 255; k+=(numInstances*octetsPerInstance))
                    {
                        List<Task> fullList = new();
                        for (int instanceNo = 0; instanceNo < numInstances; instanceNo++)
                        {
                            IPRange range = SetIPRange(i, i, j, j, k + (octetsPerInstance * instanceNo), k + ((octetsPerInstance * (instanceNo + 1)) - 1));
                            EnumerateIPAddress IPAddresses = new(range);
                            fullList.AddRange(PingRange2(IPAddresses));
                        }
                        Task.WaitAll(fullList.ToArray());
                        Console.WriteLine($"{_addrPinged} {_addrSuccess} {_elapsed.ElapsedMilliseconds / 1000.0}");
                        GC.Collect();
                    }
                }
            }

            Thread.Sleep(300000);
        }
    }


    public struct IPRange
    {
        public int StartI;
        public int EndI;
        public int StartJ;
        public int EndJ;
        public int StartK;
        public int EndK;
        public int StartL;
        public int EndL;
    }
    public class EnumerateIPAddress : IEnumerable<IPAddress>
    {
        private int _startI = 0, _endI = 0, _startJ = 0, _endJ = 0, _startK = 0, _endK = 0, _startL = 0, _endL = 0;
        public long Count { get; }

        private void CheckRange(int start, int end, ref int updateStart, ref int updateEnd)
        {
            if (start < 0)
            {
                updateStart = 0;
            }
            else
            {
                updateStart = start;
            }

            if (end > 255)
            {
                updateEnd = 255;
            }
            else
            {
                updateEnd = end;
            }

            if (start > end)
            {
                updateStart = end;
            }
        }

        public EnumerateIPAddress(IPRange range)
        {
            CheckRange(range.StartI, range.EndI, ref _startI, ref _endI);
            CheckRange(range.StartJ, range.EndJ, ref _startJ, ref _endJ);
            CheckRange(range.StartK, range.EndK, ref _startK, ref _endK);
            CheckRange(range.StartL, range.EndL, ref _startL, ref _endL);
            Count = (long)(_endI + 1 - _startI) * (long)(_endJ + 1 - _startJ) * (long)(_endK + 1 - _startK) * (long)(_endL + 1 - _startL);
        }

        public IEnumerator<IPAddress> GetEnumerator()
        {
            // Return the item at this node.
            for (int i = _startI; i <= _endI; i++)
            {
                for (int j = _startJ; j <= _endJ; j++)
                {
                    for (int k = _startK; k <= _endK; k++)
                    {
                        for (int l = _startL; l <= _endL; l++)
                        {
                            yield return new IPAddress(new byte[] { (byte)i, (byte)j, (byte)k, (byte)l });
                        }
                    }
                }
            }
        }

        System.Collections.IEnumerator
          System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
