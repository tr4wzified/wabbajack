﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.HashFunction.xxHash;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using ICSharpCode.SharpZipLib.BZip2;
using IniParser;
using IniParser.Model.Configuration;
using IniParser.Parser;
using Newtonsoft.Json;
using ReactiveUI;
using Wabbajack.Common.StatusFeed;
using Wabbajack.Common.StatusFeed.Errors;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Directory = System.IO.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Common
{
    public static partial class Utils
    {
        public static bool IsMO2Running(string mo2Path)
        {
            Process[] processList = Process.GetProcesses();
            return processList.Where(process => process.ProcessName == "ModOrganizer").Any(process => Path.GetDirectoryName(process.MainModule?.FileName) == mo2Path);
        }

        public static AbsolutePath LogFile { get; }
        public static AbsolutePath LogFolder { get; }

        public enum FileEventType
        {
            Created,
            Changed,
            Deleted
        }

        static Utils()
        {
            LogFolder = Consts.LogsFolder;
            LogFile = Consts.LogFile;
            Consts.LocalAppDataPath.CreateDirectory();
            Consts.LogsFolder.CreateDirectory();

            _startTime = DateTime.Now;

            if (LogFile.Exists)
            {
                var newPath = Consts.LogsFolder.Combine(Consts.EntryPoint.FileNameWithoutExtension + LogFile.LastModified.ToString(" yyyy-MM-dd HH_mm_ss") + ".log");
                LogFile.MoveTo(newPath, true);
            }

            var logFiles = LogFolder.EnumerateFiles(false).ToList();
            if (logFiles.Count >= Consts.MaxOldLogs)
            {
                Log($"Maximum amount of old logs reached ({logFiles.Count} >= {Consts.MaxOldLogs})");
                var filesToDelete = logFiles
                    .Where(f => f.IsFile)
                    .OrderBy(f => f.LastModified)
                    .Take(logFiles.Count - Consts.MaxOldLogs)
                    .ToList();

                Log($"Found {filesToDelete.Count} old log files to delete");

                var success = 0;
                var failed = 0;
                filesToDelete.Do(f =>
                {
                    try
                    {
                        f.Delete();
                        success++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log($"Could not delete log at {f}!\n{e}");
                    }
                });

                Log($"Deleted {success} log files, failed to delete {failed} logs");
            }

            var watcher = new FileSystemWatcher((string)Consts.LocalAppDataPath);
            AppLocalEvents = Observable.Merge(Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(h => watcher.Changed += h, h => watcher.Changed -= h).Select(e => (FileEventType.Changed, e.EventArgs)),
                                                Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(h => watcher.Created += h, h => watcher.Created -= h).Select(e => (FileEventType.Created, e.EventArgs)),
                                                Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(h => watcher.Deleted += h, h => watcher.Deleted -= h).Select(e => (FileEventType.Deleted, e.EventArgs)))
                                       .ObserveOn(RxApp.TaskpoolScheduler);
            watcher.EnableRaisingEvents = true;
        }

        private static readonly Subject<IStatusMessage> LoggerSubj = new Subject<IStatusMessage>();
        public static IObservable<IStatusMessage> LogMessages => LoggerSubj;

        private static readonly string[] Suffix = {"B", "KB", "MB", "GB", "TB", "PB", "EB"}; // Longs run out around EB

        private static object _lock = new object();

        private static DateTime _startTime;

        
        public static void Log(string msg)
        {
            Log(new GenericInfo(msg));
        }

        public static T Log<T>(T msg) where T : IStatusMessage
        {
            LogStraightToFile(string.IsNullOrWhiteSpace(msg.ExtendedDescription) ? msg.ShortDescription : msg.ExtendedDescription);
            LoggerSubj.OnNext(msg);
            return msg;
        }

        public static void Error(string errMessage)
        {
            Log(errMessage);
        }

        public static void Error(Exception ex, string? extraMessage = null)
        {
            Log(new GenericException(ex, extraMessage));
        }

        public static void ErrorThrow(Exception ex, string? extraMessage = null)
        {
            Error(ex, extraMessage);
            throw ex;
        }

        public static void Error(IException err)
        {
            LogStraightToFile($"{err.ShortDescription}\n{err.Exception.StackTrace}");
            LoggerSubj.OnNext(err);
        }

        public static void ErrorThrow(IException err)
        {
            Error(err);
            throw err.Exception;
        }

        public static void LogStraightToFile(string msg)
        {
            if (LogFile == default) return;
            lock (_lock)
            {
                LogFile.AppendAllText($"{(DateTime.Now - _startTime).TotalSeconds:0.##} - {msg}\r\n");
            }
        }

        public static void Status(string msg, Percent progress, bool alsoLog = false)
        {
            WorkQueue.AsyncLocalCurrentQueue.Value?.Report(msg, progress);
            if (alsoLog)
            {
                Utils.Log(msg);
            }
        }

        public static void Status(string msg, bool alsoLog = false)
        {
            Status(msg, Percent.Zero, alsoLog: alsoLog);
        }

        public static void CatchAndLog(Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Utils.Error(ex);
            }
        }

        public static async Task CatchAndLog(Func<Task> f)
        {
            try
            {
                await f();
            }
            catch (Exception ex)
            {
                Utils.Error(ex);
            }
        }

        public static void CopyToWithStatus(this Stream istream, long maxSize, Stream ostream, string status)
        {
            var buffer = new byte[1024 * 64];
            if (maxSize == 0) maxSize = 1;
            long totalRead = 0;
            while (true)
            {
                var read = istream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                totalRead += read;
                ostream.Write(buffer, 0, read);
                Status(status, Percent.FactoryPutInRange(totalRead, maxSize));
            }
        }
        
        public static async Task CopyToWithStatusAsync(this Stream istream, long maxSize, Stream ostream, string status)
        {
            var buffer = new byte[1024 * 64];
            if (maxSize == 0) maxSize = 1;
            long totalRead = 0;
            long remain = maxSize; 
            while (true)
            {
                var toRead = Math.Min(buffer.Length, remain);
                var read = await istream.ReadAsync(buffer, 0, (int)toRead);
                remain -= read;
                if (read == 0) break;
                totalRead += read;
                await ostream.WriteAsync(buffer, 0, read);
                Status(status, Percent.FactoryPutInRange(totalRead, maxSize));
            }

            await ostream.FlushAsync();
        }

        /// <summary>
        ///     Returns a Base64 encoding of these bytes
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string ToBase64(this byte[] data)
        {
            return Convert.ToBase64String(data);
        }

        public static string ToHex(this byte[] bytes)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }

        public static byte[] FromHex(this string hex)
        {
            return Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();
        }

        public static DateTime AsUnixTime(this long timestamp)
        {
            DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(timestamp);
            return dtDateTime;
        }
        
        public static ulong AsUnixTime(this DateTime timestamp)
        {
            var diff = timestamp - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc); 
            return (ulong)diff.TotalSeconds;
        }

        /// <summary>
        ///     Returns data from a base64 stream
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] FromBase64(this string data)
        {
            return Convert.FromBase64String(data);
        }

        /// <summary>
        ///     Executes the action for every item in coll
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="coll"></param>
        /// <param name="f"></param>
        public static void Do<T>(this IEnumerable<T> coll, Action<T> f)
        {
            foreach (var i in coll) f(i);
        }

        public static void DoIndexed<T>(this IEnumerable<T> coll, Action<int, T> f)
        {
            var idx = 0;
            foreach (var i in coll)
            {
                f(idx, i);
                idx += 1;
            }
        }
        
        public static async Task DoIndexed<T>(this IEnumerable<T> coll, Func<int, T, Task> f)
        {
            var idx = 0;
            foreach (var i in coll)
            {
                await f(idx, i);
                idx += 1;
            }
        }

        public static Task PDoIndexed<T>(this IEnumerable<T> coll, WorkQueue queue, Action<int, T> f)
        {
            return coll.Zip(Enumerable.Range(0, int.MaxValue), (v, idx) => (v, idx))
                       .PMap(queue, vs=> f(vs.idx, vs.v));
        }


        private static IniDataParser IniParser()
        {
            var config = new IniParserConfiguration {AllowDuplicateKeys = true, AllowDuplicateSections = true};
            var parser = new IniDataParser(config);
            return parser;
        }


        /// <summary>
        ///     Loads INI data from the given filename and returns a dynamic type that
        ///     can use . operators to navigate the INI.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        public static dynamic LoadIniFile(this AbsolutePath file)
        {
            return new DynamicIniData(new FileIniDataParser(IniParser()).ReadFile((string)file));
        }

        /// <summary>
        /// Loads a INI from the given string
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        public static dynamic LoadIniString(this string file)
        {
            return new DynamicIniData(new FileIniDataParser(IniParser()).ReadData(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(file)))));
        }


        public static bool FileExists(this string filename)
        {
            return File.Exists(filename);
        }

        public static string RelativeTo(this string file, string folder)
        {
            return file.Substring(folder.Length + 1);
        }

        /// <summary>
        ///     Returns the string compressed via BZip2
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] BZip2String(this string data)
        {
            using (var os = new MemoryStream())
            {
                using (var bz = new BZip2OutputStream(os))
                {
                    using (var bw = new BinaryWriter(bz))
                    {
                        bw.Write(data);
                    }
                }

                return os.ToArray();
            }
        }

        public static void BZip2ExtractToFile(this Stream src, string dest)
        {
            using (var os = File.Open(dest, System.IO.FileMode.Create))
            {
                os.SetLength(0);
                using (var bz = new BZip2InputStream(src))
                    bz.CopyTo(os);
            }
        }

        /// <summary>
        ///     Returns the string compressed via BZip2
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static string BZip2String(this byte[] data)
        {
            using (var s = new MemoryStream(data))
            {
                using (var bz = new BZip2InputStream(s))
                {
                    using (var bw = new BinaryReader(bz))
                    {
                        return bw.ReadString();
                    }
                }
            }
        }

        /// <summary>
        /// A combination of .Select(func).Where(v => v != default). So select and filter default values.
        /// </summary>
        /// <typeparam name="TIn"></typeparam>
        /// <typeparam name="TOut"></typeparam>
        /// <param name="coll"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static IEnumerable<TOut> Keep<TIn, TOut>(this IEnumerable<TIn> coll, Func<TIn, TOut> func) where TOut : IComparable<TOut>
        {
            return coll.Select(func).Where(v => v.CompareTo(default) != 0);
        }

        public static byte[] ReadAll(this Stream ins)
        {
            using (var ms = new MemoryStream())
            {
                ins.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static async Task<byte[]> ReadAllAsync(this Stream ins)
        {
            await using var ms = new MemoryStream();
            await ins.CopyToAsync(ms);
            return ms.ToArray();
        }
        
        public static async Task<string> ReadAllTextAsync(this Stream ins)
        {
            await using var ms = new MemoryStream();
            await ins.CopyToAsync(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static async Task<TR[]> PMap<TI, TR>(this IEnumerable<TI> coll, WorkQueue queue, StatusUpdateTracker updateTracker,
            Func<TI, TR> f)
        {
            var cnt = 0;
            var collist = coll.ToList();
            return await collist.PMap(queue, itm =>
            {
                updateTracker.MakeUpdate(collist.Count, Interlocked.Increment(ref cnt));
                return f(itm);
            });
        }

        public static async Task<TR[]> PMap<TI, TR>(this IEnumerable<TI> coll, WorkQueue queue, StatusUpdateTracker updateTracker,
            Func<TI, Task<TR>> f)
        {
            var cnt = 0;
            var collist = coll.ToList();
            return await collist.PMap(queue, itm =>
            {
                updateTracker.MakeUpdate(collist.Count, Interlocked.Increment(ref cnt));
                return f(itm);
            });
        }

        public static async Task PMap<TI>(this IEnumerable<TI> coll, WorkQueue queue, StatusUpdateTracker updateTracker,
            Func<TI, Task> f)
        {
            var cnt = 0;
            var collist = coll.ToList();
            await collist.PMap(queue, async itm =>
            {
                updateTracker.MakeUpdate(collist.Count, Interlocked.Increment(ref cnt));
                await f(itm);
            });
        }

        public static async Task PMap<TI>(this IEnumerable<TI> coll, WorkQueue queue, StatusUpdateTracker updateTracker,
            Action<TI> f)
        {
            var cnt = 0;
            var collist = coll.ToList();
            await collist.PMap(queue, itm =>
            {
                updateTracker.MakeUpdate(collist.Count, Interlocked.Increment(ref cnt));
                f(itm);
                return true;
            });
        }

        public static async Task<TR[]> PMap<TI, TR>(this IEnumerable<TI> coll, WorkQueue queue,
            Func<TI, TR> f)
        {
            var colllst = coll.ToList();

            var remainingTasks = colllst.Count;

            var tasks = colllst.Select(i =>
            {
                var tc = new TaskCompletionSource<TR>();
                queue.QueueTask(async () =>
                {
                    try
                    {
                        tc.SetResult(f(i));
                    }
                    catch (Exception ex)
                    {
                        tc.SetException(ex);
                    }
                    Interlocked.Decrement(ref remainingTasks);
                });
                return tc.Task;
            }).ToList();

            // To avoid thread starvation, we'll start to help out in the work queue
            if (WorkQueue.WorkerThread)
            {
                while (true)
                {
                    var (got, a) = await queue.Queue.TryTake(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                    if (got)
                    {
                        await a();
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return await Task.WhenAll(tasks);
        }
        public static async Task<TR[]> PMap<TI, TR>(this IEnumerable<TI> coll, WorkQueue queue,
            Func<TI, Task<TR>> f)
        {
            var colllst = coll.ToList();

            var remainingTasks = colllst.Count;

            var tasks = colllst.Select(i =>
            {
                var tc = new TaskCompletionSource<TR>();
                queue.QueueTask(async () =>
                {
                    try
                    {
                        tc.SetResult(await f(i));
                    }
                    catch (Exception ex)
                    {
                        tc.SetException(ex);
                    }
                    Interlocked.Decrement(ref remainingTasks);
                });
                return tc.Task;
            }).ToList();

            // To avoid thread starvation, we'll start to help out in the work queue
            if (WorkQueue.WorkerThread)
            {
                while (remainingTasks > 0)
                {
                    var (got, a) = await queue.Queue.TryTake(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                    if (got)
                    {
                        await a();
                    }
                }
            }

            return await Task.WhenAll(tasks);
        }

        public static async Task PMap<TI>(this IEnumerable<TI> coll, WorkQueue queue,
            Func<TI, Task> f)
        {
            var colllst = coll.ToList();

            var remainingTasks = colllst.Count;

            var tasks = colllst.Select(i =>
            {
                var tc = new TaskCompletionSource<bool>();
                queue.QueueTask(async () =>
                {
                    try
                    {
                        await f(i);
                        tc.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tc.SetException(ex);
                    }
                    Interlocked.Decrement(ref remainingTasks);
                });
                return tc.Task;
            }).ToList();
            
            // To avoid thread starvation, we'll start to help out in the work queue
            if (WorkQueue.WorkerThread)
            {
                while (remainingTasks > 0)
                {
                    var (got, a) = await queue.Queue.TryTake(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                    if (got)
                    {
                        await a();
                    }
                }
            }

            await Task.WhenAll(tasks);
        }

        public static async Task PMap<TI>(this IEnumerable<TI> coll, WorkQueue queue, Action<TI> f)
        {
            await coll.PMap(queue, i =>
            {
                f(i);
                return false;
            });
        }

        public static void DoProgress<T>(this IEnumerable<T> coll, string msg, Action<T> f)
        {
            var lst = coll.ToList();
            lst.DoIndexed((idx, i) =>
            {
                Status(msg, Percent.FactoryPutInRange(idx, lst.Count));
                f(i);
            });
        }
        
        public static async Task DoProgress<T>(this IEnumerable<T> coll, string msg, Func<T, Task> f)
        {
            var lst = coll.ToList();
            await lst.DoIndexed(async (idx, i) =>
            {
                Status(msg, Percent.FactoryPutInRange(idx, lst.Count));
                await f(i);
            });
        }

        public static void OnQueue(Action f)
        {
            new List<bool>().Do(_ => f());
        }

        public static async Task<Stream> PostStream(this HttpClient client, string url, HttpContent content)
        {
            var result = await client.PostAsync(url, content);
            return await result.Content.ReadAsStreamAsync();
        }

        public static IEnumerable<T> DistinctBy<T, V>(this IEnumerable<T> vs, Func<T, V> select)
        {
            var set = new HashSet<V>();
            foreach (var v in vs)
            {
                var key = select(v);
                if (set.Contains(key)) continue;
                set.Add(key);
                yield return v;
            }
        }

        public static T Last<T>(this T[] a)
        {
            if (a == null || a.Length == 0)
                throw new InvalidDataException("null or empty array");
            return a[a.Length - 1];
        }

        [return: MaybeNull]
        public static V GetOrDefault<K, V>(this IDictionary<K, V> dict, K key)
            where K : notnull
        {
            if (dict.TryGetValue(key, out var v)) return v;
            return default;
        }

        public static string ToFileSizeString(this long byteCount)
        {
            if (byteCount == 0)
                return "0" + Suffix[0];
            var bytes = Math.Abs(byteCount);
            var place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            var num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return Math.Sign(byteCount) * num + Suffix[place];
        }

        public static string ToFileSizeString(this int byteCount)
        {
            return ToFileSizeString((long)byteCount);
        }

        public static async Task CreatePatch(byte[] a, byte[] b, Stream output)
        {
            var dataA = a.xxHash().ToHex();
            var dataB = b.xxHash().ToHex();
            var cacheFile = Consts.PatchCacheFolder.Combine($"{dataA}_{dataB}.patch");
            Consts.PatchCacheFolder.CreateDirectory();

            while (true)
            {
                if (cacheFile.IsFile)
                {
                    RETRY_OPEN:
                    try
                    {
                        await using var f = cacheFile.OpenRead();
                        await f.CopyToAsync(output);
                    }
                    catch (IOException)
                    {
                        // Race condition with patch caching
                        await Task.Delay(100);
                        goto RETRY_OPEN;
                    }

                }
                else
                {
                    var tmpName = Consts.PatchCacheFolder.Combine(Guid.NewGuid() + ".tmp");

                    await using (var f = tmpName.Create())
                    {
                        Status("Creating Patch");
                        OctoDiff.Create(a, b, f);
                    }

                    RETRY:
                    try
                    {
                        tmpName.MoveTo(cacheFile, true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (cacheFile.IsFile)
                            continue;
                        await Task.Delay(1000);
                        goto RETRY;
                    }

                    continue;
                }

                break;
            }
        }

        public static async Task CreatePatch(Stream srcStream, Hash srcHash, FileStream destStream, Hash destHash,
            FileStream patchStream)
        {
            await using var sigFile = new TempStream();
            OctoDiff.Create(srcStream, destStream, sigFile, patchStream);
            patchStream.Position = 0;
            var tmpName = Consts.PatchCacheFolder.Combine(Guid.NewGuid() + ".tmp");

            await using (var f = tmpName.Create())
            {
                await patchStream.CopyToAsync(f);
                patchStream.Position = 0;
            }
            
            try
            {
                var cacheFile = Consts.PatchCacheFolder.Combine($"{srcHash.ToHex()}_{destHash.ToHex()}.patch");
                Consts.PatchCacheFolder.CreateDirectory();

                tmpName.MoveTo(cacheFile, true);
            }
            catch (UnauthorizedAccessException)
            {
                tmpName.Delete();
            }
        }

        public static bool TryGetPatch(Hash foundHash, Hash fileHash, [MaybeNullWhen(false)] out byte[] ePatch)
        {
            var patchName = Consts.PatchCacheFolder.Combine($"{foundHash.ToHex()}_{fileHash.ToHex()}.patch");
            if (patchName.Exists)
            {
                ePatch = patchName.ReadAllBytes();
                return true;
            }

            ePatch = Array.Empty<byte>();
            return false;
        }

        public static void ApplyPatch(Stream input, Func<Stream> openPatchStream, Stream output)
        {
            using var ps = openPatchStream();
            using var br = new BinaryReader(ps);
            var bytes = br.ReadBytes(8);
            var str = Encoding.ASCII.GetString(bytes);
            switch (str)
            {
                case "BSDIFF40":
                    BSDiff.Apply(input, openPatchStream, output);
                    return;
                case "OCTODELT":
                    OctoDiff.Apply(input, openPatchStream, output);
                    return;
                default:
                    throw new Exception($"No diff dispatch for: {str}");
            }

        }

        public static IEnumerable<T> ButLast<T>(this IEnumerable<T> coll)
        {
            var lst = coll.ToList();
            return lst.Take(lst.Count() - 1);
        }

        public static byte[] ConcatArrays(this IEnumerable<byte[]> arrays)
        {
            var outarr = new byte[arrays.Sum(a => a.Length)];
            int offset = 0;
            foreach (var arr in arrays)
            {
                Array.Copy(arr, 0, outarr, offset, arr.Length);
                offset += arr.Length;
            }
            return outarr;
        }

        /// <summary>
        /// Roundtrips the value through the JSON routines
        /// </summary>
        /// <typeparam name="TV"></typeparam>
        /// <typeparam name="TR"></typeparam>
        /// <param name="tv"></param>
        /// <returns></returns>
        public static T ViaJSON<T>(this T tv)
        {
            var json = tv.ToJson();
            return json.FromJsonString<T>();
        }

        /*
        public static void Error(string msg)
        {
            Log(msg);
            throw new Exception(msg);
        }*/

        public static Stream GetEmbeddedResourceStream(string name)
        {
            return (from assembly in AppDomain.CurrentDomain.GetAssemblies()
                    where !assembly.IsDynamic
                    from rname in assembly.GetManifestResourceNames()
                    where rname == name
                    select assembly.GetManifestResourceStream(name)).First();
        }

        public static T FromYaml<T>(this Stream s)
        {
            var d = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();
            return d.Deserialize<T>(new StreamReader(s));
        }

        public static T FromYaml<T>(this string s)
        {
            var d = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            return d.Deserialize<T>(new StringReader(s));
        }
        public static void LogStatus(string s)
        {
            Status(s);
            Log(s);
        }

        private static async Task<long> TestDiskSpeedInner(WorkQueue queue, AbsolutePath path)
        {
            var startTime = DateTime.Now;
            var seconds = 5;
            var results = await Enumerable.Range(0, queue.DesiredNumWorkers)
                .PMap(queue, idx =>
                {
                    var random = new Random();

                    var file = path.Combine($"size_test{idx}.bin");
                    long size = 0;
                    byte[] buffer = new byte[1024 * 8];
                    random.NextBytes(buffer);
                    using (var fs = file.Create())
                    {
                        while (DateTime.Now < startTime + new TimeSpan(0, 0, seconds))
                        {
                            fs.Write(buffer, 0, buffer.Length);
                            // Flush to make sure large buffers don't cause the rate to be higher than it should
                            fs.Flush();
                            size += buffer.Length;
                        }
                    }
                    file.Delete();
                    return size;
                });
            return results.Sum() / seconds;
        }

        public static async Task<long> TestDiskSpeed(WorkQueue queue, AbsolutePath path)
        {
            var benchmarkFile = path.Combine("disk_benchmark.bin");
            if (benchmarkFile.Exists)
            {
                try
                {
                    return benchmarkFile.FromJson<long>();
                }
                catch (Exception ex)
                {
                    // ignored
                }
            }
            var speed = await TestDiskSpeedInner(queue, path);
            speed.ToJson(benchmarkFile);
           
            return speed;
        }

        /// https://stackoverflow.com/questions/422090/in-c-sharp-check-that-filename-is-possibly-valid-not-that-it-exists
        public static IErrorResponse IsFilePathValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ErrorResponse.Fail("Path is empty.");
            }
            try
            {
                var fi = new System.IO.FileInfo(path);
            }
            catch (ArgumentException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            catch (PathTooLongException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            return ErrorResponse.Success;
        }

        public static IErrorResponse IsDirectoryPathValid(AbsolutePath path)
        {
            if (path == default)
            {
                return ErrorResponse.Fail("Path is empty");
            }
            try
            {
                var fi = new System.IO.DirectoryInfo((string)path);
            }
            catch (ArgumentException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            catch (PathTooLongException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return ErrorResponse.Fail(ex.Message);
            }
            return ErrorResponse.Success;
        }

        /// <summary>
        /// Both AlphaFS and C#'s Directory.Delete sometimes fail when certain files are read-only
        /// or have other weird attributes. This is the only 100% reliable way I've found to completely
        /// delete a folder. If you don't like this code, it's unlikely to change without a ton of testing.
        /// </summary>
        /// <param name="path"></param>
        public static async Task DeleteDirectory(AbsolutePath path)
        {
            if (!path.Exists)
                return;

            var process = new ProcessHelper
            {
                Path = ((RelativePath)"cmd.exe").RelativeToSystemDirectory(),
                Arguments = new object[] {"/c", "del", "/f", "/q", "/s", $"\"{(string)path}\"", "&&", "rmdir", "/q", "/s", $"\"{(string)path}\""},
            };
            var result = process.Output.Where(d => d.Type == ProcessHelper.StreamType.Output)
                .ForEachAsync(p =>
                {
                    Status($"Deleting: {p.Line}");
                });

            var exitCode = await process.Start();
            await result;
        }

        public static bool IsUnderneathDirectory(string path, string dirPath)
        {
            return path.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Writes a file to JSON but in an encrypted format in the user's app local directory.
        /// The data will be encrypted so that it can only be read by this machine and this user.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="data"></param>
        public static void ToEcryptedJson<T>(this T data, string key)
        {
            var bytes = Encoding.UTF8.GetBytes(data.ToJson());
            bytes.ToEcryptedData(key);
        }

        public static T FromEncryptedJson<T>(string key)
        {
            var decoded = FromEncryptedData(key);
            return Encoding.UTF8.GetString(decoded).FromJsonString<T>();
        }

        
        public static void ToEcryptedData(this byte[] bytes, string key)
        {
            var encoded = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(key), DataProtectionScope.LocalMachine);
            Consts.LocalAppDataPath.CreateDirectory();
            
            Consts.LocalAppDataPath.Combine(key).WriteAllBytes(encoded);
        }
        public static byte[] FromEncryptedData(string key)
        {
            var bytes = Consts.LocalAppDataPath.Combine(key).ReadAllBytes();
            return ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes(key), DataProtectionScope.LocalMachine);
        }

        public static bool HaveEncryptedJson(string key)
        {
            return Consts.LocalAppDataPath.Combine(key).IsFile;
        }

        public static IObservable<(FileEventType, FileSystemEventArgs)> AppLocalEvents { get; }

        public static IObservable<bool> HaveEncryptedJsonObservable(string key)
        {
            var path = Consts.LocalAppDataPath.Combine(key);
            return AppLocalEvents.Where(t => (AbsolutePath)t.Item2.FullPath.ToLower() == path)
                                 .Select(_ => path.Exists)
                                 .StartWith(path.Exists)
                                 .DistinctUntilChanged();
        }

        public static void DeleteEncryptedJson(string key)
        {
            Consts.LocalAppDataPath.Combine(key).Delete();
        }

        public static void StartProcessFromFile(string file)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {file}")
            {
                CreateNoWindow = true,
            });
        }

        public static void OpenWebsite(Uri url)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start {url}")
            {
                CreateNoWindow = true,
            });
        }

        public static bool IsInPath(this string path, string parent)
        {
            return path.ToLower().TrimEnd('\\').StartsWith(parent.ToLower().TrimEnd('\\') + "\\");
        }
        
        public static async Task CopyToLimitAsync(this Stream frm, Stream tw, long limit)
        {
            var buff = new byte[1024];
            while (limit > 0)
            {
                var to_read = Math.Min(buff.Length, limit);
                var read = await frm.ReadAsync(buff, 0, (int)to_read);
                await tw.WriteAsync(buff, 0, read);
                limit -= read;
            }

            tw.Flush();
        }

        public class NexusErrorResponse
        {
            public int code;
            public string message = string.Empty;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }


        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static MEMORYSTATUSEX GetMemoryStatus()
        {
            var mstat = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(mstat);
            return mstat;
        }

        public static string MakeRandomKey()
        {
            var random = new Random();
            byte[] bytes = new byte[32];
            random.NextBytes(bytes);
            return bytes.ToHex();
        }
        
        public static byte[] RandomData(int size)
        {
            var random = new Random();
            byte[] bytes = new byte[size];
            random.NextBytes(bytes);
            return bytes;
        }

        public static async Task CopyFileAsync(string src, string dest)
        {
            await using var s = File.OpenRead(src);
            await using var d = File.Create(dest);
            await s.CopyToAsync(d);
        }

        public static string ToNormalString(this SecureString value)
        {
            var valuePtr = IntPtr.Zero;
            try
            {
                valuePtr = Marshal.SecureStringToGlobalAllocUnicode(value);
                return Marshal.PtrToStringUni(valuePtr) ?? "";
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(valuePtr);
            }
        }

        public static IEnumerable<IEnumerable<T>> Partition<T>(this IEnumerable<T> coll, int size)
        {
            var lst = new List<T>();
            foreach (var itm in coll)
            {
                lst.Add(itm);
                if (lst.Count != size) continue;

                yield return lst;
                lst = new List<T>();
            }

            if (lst.Count > 0 && lst.Count != size)
                yield return lst;
        }
    }
}
