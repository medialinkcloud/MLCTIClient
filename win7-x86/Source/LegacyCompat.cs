// Win7 SP1 / .NET Framework 4.8 compatibility helpers.
// All network and dial validation behavior must remain equivalent to RC4.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}
namespace System
{
    internal static class LegacyStringExtensions
    {
        internal static bool Contains(this string value, string fragment, StringComparison mode) =>
            value != null && fragment != null && value.IndexOf(fragment, mode) >= 0;
        internal static bool Contains(this string value, char fragment) => value.IndexOf(fragment) >= 0;
        internal static bool StartsWith(this string value, char fragment) => value.Length > 0 && value[0] == fragment;
        internal static bool EndsWith(this string value, char fragment) => value.Length > 0 && value[value.Length - 1] == fragment;
        internal static string[] Split(this string value, char separator) => value.Split(new[] { separator });
        internal static string[] Split(this string value, char separator, StringSplitOptions options) =>
            value.Split(new[] { separator }, options);
    }
}
namespace System.Collections.Generic
{
    internal static class LegacyDictionaryExtensions
    {
        internal static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair,
            out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
        internal static TValue GetValueOrDefault<TKey,TValue>(this IReadOnlyDictionary<TKey,TValue> map, TKey key, TValue fallback = default(TValue)) =>
            map.TryGetValue(key, out TValue value) ? value : fallback;
    }
}
namespace System.Linq
{
    internal static class LegacyLinqExtensions
    {
        internal static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
            var seen = new HashSet<TKey>();
            foreach (var value in source)
                if (seen.Add(keySelector(value))) yield return value;
        }
        internal static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            using (var iterator = source.GetEnumerator())
            {
                while (true)
                {
                    var batch = new List<T>(size);
                    while (batch.Count < size && iterator.MoveNext()) batch.Add(iterator.Current);
                    if (batch.Count == 0) yield break;
                    yield return batch.ToArray();
                }
            }
        }
    }
}
namespace System.Threading.Tasks
{
    internal static class LegacyTaskExtensions
    {
        internal static async Task WithCancellation(this Task task, CancellationToken cancellation)
        {
            if (task.IsCompleted) { await task.ConfigureAwait(false); return; }
            if (!cancellation.CanBeCanceled) { await task.ConfigureAwait(false); return; }
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => signal.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, signal.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellation);
            }
            await task.ConfigureAwait(false);
        }
        internal static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellation)
        {
            if (task.IsCompleted) return await task.ConfigureAwait(false);
            if (!cancellation.CanBeCanceled) return await task.ConfigureAwait(false);
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => signal.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, signal.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellation);
            }
            return await task.ConfigureAwait(false);
        }
    }
}
namespace MedialinkCTIClient
{
    internal static class LegacyCompat
    {
        // .NET Framework 4.8 lacks CryptographicOperations.ZeroMemory.
        // Array.Clear is the available managed equivalent; do not log byte buffers.
        internal static void ZeroMemory(byte[] buffer)
        {
            if (buffer != null) Array.Clear(buffer, 0, buffer.Length);
        }
        internal static long UptimeMilliseconds =>
            (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        internal static int Clamp(int number, int min, int max) => Math.Min(max, Math.Max(min, number));
        internal static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';
        internal static string Md5Hex(string payload)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
        }
        internal static void MoveOverwrite(string temporary, string destination)
        {
            // File.Replace is atomic for existing file; protect the previous config if interrupted.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string value);
        internal static void SetCueBanner(TextBox textbox, string text)
        {
            if (textbox == null) return;
            SendMessage(textbox.Handle, 0x1501, IntPtr.Zero, text);
        }
    }
}
