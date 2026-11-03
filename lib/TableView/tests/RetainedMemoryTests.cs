using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Measures the managed memory a loaded, sorted table with an identity key keeps for each row, once
/// the host has published its whole list again: the heap's growth from a table of 10,000 rows to one
/// of 100,000, with the rows themselves counted out. It also gives each of the table's fields the
/// objects reached first through it, so the growth has owners. It asserts no limit; compare builds
/// on one machine.
/// </summary>
[TestClass]
public class RetainedMemoryTests
{
    private const int Small = 10_000;
    private const int Large = 100_000;

    public TestContext TestContext { get; set; } = null!;

    private sealed class MemoryRow(int key)
    {
        public int Key { get; } = key;
        public string Name { get; } = "row " + key * 7919 % 100_003;
        public int Rank { get; } = key * 7919 % 10_007;
    }

    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow("names")]
    [DataRow("numbers")]
    public Task BytesPerRow(string key) =>
        TestHost.RunAsync(async () =>
        {
            var small = await MeasureAsync(Small, key);
            var large = await MeasureAsync(Large, key);
            double rows = Large - Small;
            TestContext.WriteLine($"retained {key}: heap {(large.Heap - small.Heap) / rows:0.0} bytes per row "
                + $"({small.Heap} bytes at {Small} rows, {large.Heap} at {Large}); "
                + $"attributed {(large.Owners.Values.Sum() - small.Owners.Values.Sum()) / rows:0.0} bytes per row");
            foreach (string owner in large.Owners.Keys.Union(small.Owners.Keys).Order())
            {
                long growth = large.Owners.GetValueOrDefault(owner) - small.Owners.GetValueOrDefault(owner);
                if (growth >= rows / 2)
                {
                    TestContext.WriteLine($"  {owner}: {growth / rows:0.0} bytes per row");
                }
            }
        });

    private static async Task<(long Heap, Dictionary<string, long> Owners)> MeasureAsync(int size, string key)
    {
        await SettleAsync();
        List<MemoryRow> rows = Enumerable.Range(0, size).Select(index => new MemoryRow(index)).ToList();
        Column names = TestData.Column("names", 200);
        Column numbers = TestData.Column("numbers", 200);
        Table table = TestData.Table(names, numbers);
        table.Schema<MemoryRow>().Key(row => row.Key)
            .SortKey(names, row => row.Name, StringComparer.CurrentCulture)
            .SortKey(numbers, row => row.Rank);
        table.Width = 700;
        table.Height = 600;
        MemoryRow[] again = rows.ToArray();
        long withRows = Heap();
        table.ItemsSource = rows;
        await TableHarness.LoadAsync(table);
        table.Sort = new(key == "names" ? names : numbers);
        await Task.Delay(200);
        // A host that assigns its list again, as section 5.3 asks of a list that does not notify,
        // publishes the whole source; after two of them every kept structure has been used.
        table.ItemsSource = again;
        await Task.Delay(200);
        table.ItemsSource = rows;
        await Task.Delay(500);
        long loaded = Heap();
        Dictionary<string, long> owners = Attribute(table, rows, again);
        TestHost.RootPanel.Children.Remove(table);
        await Task.Delay(200);
        GC.KeepAlive(rows);
        return (loaded - withRows, owners);
    }

    /// <summary>
    /// Wait until a table removed earlier has been released: the tree lets go of it over the next
    /// ticks, and a reading taken before then counts it.
    /// </summary>
    private static async Task SettleAsync()
    {
        long previous = Heap();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(200);
            long current = Heap();
            if (Math.Abs(current - previous) < 64 * 1024)
            {
                return;
            }
            previous = current;
        }
    }

    private static long Heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    /// <summary>The table's parts that own rows' state, broken down first so shared objects count for them.</summary>
    private static readonly string[] Owners = { "_source", "_view", "_sortedOrder", "_hierarchy", "_selection" };

    /// <summary>
    /// The bytes of the objects each table field reaches, not counting rows, strings, visual
    /// elements, delegates or platform objects. A part of the table is broken down by its own fields,
    /// two levels deep. An object reached twice counts once, for the field reached first: the
    /// owners above, then the other fields in name order. The host's own lists are not the table's.
    /// </summary>
    private static Dictionary<string, long> Attribute(Table table, params object[] host)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance) { table };
        seen.UnionWith(host);
        Dictionary<string, long> owners = new();
        IEnumerable<FieldInfo> fields = Fields(typeof(Table))
            .OrderBy(field => Array.IndexOf(Owners, field.Name) is int rank && rank >= 0 ? rank : Owners.Length)
            .ThenBy(field => field.Name);
        foreach (FieldInfo field in fields)
        {
            Breakdown(field.Name, field.GetValue(table), 2, owners, seen);
        }
        return owners;
    }

    private static void Breakdown(string path, object? value, int depth, Dictionary<string, long> owners,
        HashSet<object> seen)
    {
        if (depth > 0 && value is not null && IsPart(value.GetType()) && !Skips(value) && seen.Add(value))
        {
            owners[path] = ObjectSize(value.GetType());
            foreach (FieldInfo field in Fields(value.GetType()).OrderBy(field => field.Name))
            {
                Breakdown($"{path}.{field.Name}", field.GetValue(value), depth - 1, owners, seen);
            }
            return;
        }
        owners[path] = owners.GetValueOrDefault(path) + Size(value, seen);
    }

    private static bool IsPart(Type type) =>
        type.Assembly == typeof(Table).Assembly && !type.IsValueType && !type.IsArray;

    /// <summary>Objects that are not the table's own storage: rows and their text, and platform objects.</summary>
    private static bool Skips(object value) =>
        value is MemoryRow or string or DependencyObject or Delegate or MemberInfo or Task or Thread
        || value.GetType().Namespace is string space && (space.StartsWith("Microsoft.UI", StringComparison.Ordinal)
            || space.StartsWith("Windows.", StringComparison.Ordinal) || space.StartsWith("WinRT", StringComparison.Ordinal)
            || space.StartsWith("System.Threading", StringComparison.Ordinal)
            || space.StartsWith("System.Globalization", StringComparison.Ordinal));

    private static long Size(object? value, HashSet<object> seen)
    {
        if (value is null || Skips(value) || !seen.Add(value))
        {
            return 0;
        }
        Type type = value.GetType();
        if (value is Array array)
        {
            Type element = type.GetElementType()!;
            long bytes = 24 + (long)SizeOf(element) * array.Length;
            if (!element.IsValueType || Walks(element))
            {
                foreach (object? item in array)
                {
                    bytes += element.IsValueType ? Inside(item!, element, seen) : Size(item, seen);
                }
            }
            return bytes;
        }
        return type.IsPointer ? 0 : ObjectSize(type) + Inside(value, type, seen);
    }

    /// <summary>What the reference fields of <paramref name="value"/> reach.</summary>
    private static long Inside(object value, Type type, HashSet<object> seen)
    {
        long bytes = 0;
        foreach (FieldInfo field in Fields(type))
        {
            if (field.FieldType.IsValueType)
            {
                if (Walks(field.FieldType))
                {
                    bytes += Inside(field.GetValue(value)!, field.FieldType, seen);
                }
            }
            else if (!field.FieldType.IsPointer)
            {
                bytes += Size(field.GetValue(value), seen);
            }
        }
        return bytes;
    }

    private static readonly Dictionary<Type, bool> WalkCache = new();

    /// <summary>Whether a value of <paramref name="type"/> can hold an array or a collection.</summary>
    private static bool Walks(Type type)
    {
        if (!WalkCache.TryGetValue(type, out bool walks))
        {
            WalkCache[type] = false;
            walks = !type.IsPrimitive && !type.IsEnum && !type.IsPointer && Fields(type).Any(field =>
                field.FieldType.IsValueType ? Walks(field.FieldType) : field.FieldType != typeof(string));
            WalkCache[type] = walks;
        }
        return walks;
    }

    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
            if (current == typeof(Table))
            {
                yield break;
            }
        }
    }

    private static long ObjectSize(Type type) =>
        Math.Max(24, (16 + Fields(type).Sum(field => (long)SizeOf(field.FieldType)) + 7) / 8 * 8);

    private static readonly Dictionary<Type, int> SizeCache = new();

    private static int SizeOf(Type type)
    {
        if (!type.IsValueType)
        {
            return IntPtr.Size;
        }
        if (!SizeCache.TryGetValue(type, out int size))
        {
            size = (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
            SizeCache[type] = size;
        }
        return size;
    }
}
