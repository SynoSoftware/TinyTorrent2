using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 5.3: "The implementation may update a private view incrementally or rebuild it, but the
/// observable result MUST be the same." Two tables start in the same state. One receives a source
/// change as a collection notification, the other receives the same change through a whole
/// publish: a plain list assigned again, or <c>RefreshView()</c> for changed values. The two must then show the same rows, as the same instances, with the
/// same held rows, selection and pending settle, and must still agree once the settle has run.
/// </summary>
[TestClass]
public class PipelineEquivalenceTests
{
    private static int _serial;

    public sealed class EqRow(string key, int rank)
    {
        public int Serial { get; } = Interlocked.Increment(ref _serial);
        public string Key { get; } = key;
        public int Rank { get; set; } = rank;
        public string Name { get; set; } = ((char)('a' + rank / 10)).ToString();
        public bool IsExpanded { get; set; } = true;
        public ResetList Live { get; } = new();
        public List<EqRow> Plain { get; } = new();
        public override string ToString() => Key;
    }

    /// <summary>A notifying list that can also change without a notification, then raise Reset.</summary>
    public sealed class ResetList : ObservableCollection<EqRow>
    {
        internal void InsertQuietly(int index, EqRow row) => Items.Insert(index, row);

        internal void RaiseReset() => OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public enum Kind { Add, Remove, ReplaceSame, ReplaceNew, ReplaceThenAdd, Move, Reset, Values, Return, ReturnThenPublish }

    /// <summary>
    /// Each mode is its own test, so a defect that ends the test process hides no other mode.
    /// Modes: unsorted; sorted inside the settle interval; sorted with moves allowed; a pointed row
    /// pinned off its sorted place, then the pointer gone; a row held under the pointer, sorted and
    /// unsorted; a key change
    /// withheld inside the interval under a string sort; a sorted hierarchy inside the interval and
    /// with moves allowed; equal keys, where source order alone orders the rows.
    /// </summary>
    [TestMethod]
    public Task Unsorted() => RunAsync("unsorted");

    [TestMethod]
    public Task SortedInsideTheInterval() => RunAsync("sorted");

    [TestMethod]
    public Task SortedWithMovesAllowed() => RunAsync("sorted-moves");

    [TestMethod]
    public Task AfterAPinnedRowWasReleased() => RunAsync("pinned");

    [TestMethod]
    public Task WhileARowIsHeld() => RunAsync("held");

    [TestMethod]
    public Task WhileARowIsHeldUnsorted() => RunAsync("held-unsorted");

    [TestMethod]
    public Task WithAWithheldStringKeyChange() => RunAsync("withheld");

    [TestMethod]
    public Task SortedHierarchy() => RunAsync("hierarchy");

    [TestMethod]
    public Task SortedHierarchyWithMovesAllowed() => RunAsync("hierarchy-moves");

    [TestMethod]
    public Task SortedWithEqualKeys() => RunAsync("ties");

    private static Task RunAsync(string mode) =>
        TestHost.RunAsync(async () =>
        {
            Kind[] kinds = Enum.GetValues<Kind>()
                .Where(kind => kind is not (Kind.Return or Kind.ReturnThenPublish) || mode.StartsWith("held", StringComparison.Ordinal))
                .ToArray();
            List<Pair> pairs = new();
            foreach (Kind kind in kinds)
            {
                pairs.Add(await Pair.LoadAsync(mode, kind));
            }
            List<string> failures = new();
            List<Pair> settling = new();
            foreach (Pair pair in pairs)
            {
                try
                {
                    pair.Setup();
                    Compare(pair, "after setup", failures);
                    pair.Apply();
                    Compare(pair, "at once", failures);
                    settling.Add(pair);
                }
                catch (Exception error)
                {
                    failures.Add($"{pair.Kind}: {error.GetType().Name}: {error.Message}");
                }
            }
            // Past the two-second interval, so any pending settle has run.
            await Task.Delay(2600);
            foreach (Pair pair in settling)
            {
                try
                {
                    Compare(pair, "after the settle", failures);
                }
                catch (Exception error)
                {
                    failures.Add($"{pair.Kind} after the settle: {error.GetType().Name}: {error.Message}");
                }
            }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        });

    private static void Compare(Pair pair, string when, List<string> failures)
    {
        string incremental = State(pair.Incremental), whole = State(pair.Whole);
        if (incremental != whole)
        {
            failures.Add($"{pair.Kind} {when}: notification [{incremental}] whole [{whole}]");
        }
    }

    /// <summary>The rows in view order, each with its instance and whether it is held; the selection; the pending settle.</summary>
    private static string State(Table table)
    {
        IEnumerable view = (IEnumerable)SelectionHarness.Descendant<Microsoft.UI.Xaml.Controls.ListView>(table)!.ItemsSource;
        string rows = string.Join(",", view.Cast<EqRow>().Select(row =>
            $"{row.Key}#{row.Serial}{((bool)Proof.Call(table, "IsHeld", row)! ? "*" : "")}"));
        string selection = string.Join(",", table.Selection.Items.Cast<EqRow>().Select(row => row.Key));
        return $"{rows} | selected {selection} current {(table.Selection.Current as EqRow)?.Key} "
            + $"| settle {Proof.Field<bool>(table, "_settlePending")}";
    }

    private sealed class Pair
    {
        private readonly string _mode;
        private readonly EqRow[] _rows;
        private readonly ResetList _live;
        private readonly List<EqRow> _plain;
        private readonly EqRow[] _roots;

        private Pair(string mode, Kind kind, Table incremental, Table whole, EqRow[] rows, ResetList live,
            List<EqRow> plain, EqRow[] roots)
        {
            _mode = mode;
            Kind = kind;
            Incremental = incremental;
            Whole = whole;
            _rows = rows;
            _live = live;
            _plain = plain;
            _roots = roots;
        }

        internal Kind Kind { get; }
        internal Table Incremental { get; }
        internal Table Whole { get; }

        private bool IsHierarchy => IsHierarchyMode(_mode);

        private static bool IsHierarchyMode(string mode) => mode.StartsWith("hierarchy", StringComparison.Ordinal);

        /// <summary>The held row's index, which a returning row takes while the pointer stays.</summary>
        private int _heldIndex = -1;

        internal static async Task<Pair> LoadAsync(string mode, Kind kind)
        {
            int[] ranks = mode == "ties" ? Enumerable.Repeat(10, 8).ToArray() : new[] { 50, 10, 70, 30, 0, 60, 20, 40 };
            EqRow[] rows = ranks.Select((rank, index) => new EqRow("k" + index, rank)).ToArray();
            EqRow[] roots = Array.Empty<EqRow>();
            ResetList live;
            List<EqRow> plain;
            if (IsHierarchyMode(mode))
            {
                // Three expanded roots; the middle one's six children are the changing list.
                roots = new[] { new EqRow("r0", 15), new EqRow("r1", 5), new EqRow("r2", 25) };
                for (int index = 0; index < rows.Length; index++)
                {
                    EqRow parent = roots[index < 6 ? 1 : index - 6 == 0 ? 0 : 2];
                    parent.Live.Add(rows[index]);
                    parent.Plain.Add(rows[index]);
                }
                live = roots[1].Live;
                plain = roots[1].Plain;
            }
            else
            {
                live = new();
                plain = new();
                foreach (EqRow row in rows)
                {
                    live.Add(row);
                    plain.Add(row);
                }
            }
            Table incremental = await LoadTableAsync(mode, notifying: true, IsHierarchyMode(mode) ? roots : live);
            Table whole = await LoadTableAsync(mode, notifying: false, IsHierarchyMode(mode) ? roots.ToArray() : plain);
            return new(mode, kind, incremental, whole, rows, live, plain, roots);
        }

        private static async Task<Table> LoadTableAsync(string mode, bool notifying, IEnumerable source)
        {
            Column a = TestData.Column("a", 200);
            Table table = TestData.Table(a, TestData.Column("b", 200));
            Schema<EqRow> schema = table.Schema<EqRow>().Key(row => row.Key);
            if (mode == "withheld")
            {
                schema.SortKey(a, row => row.Name);
            }
            else if (mode is not ("unsorted" or "held-unsorted"))
            {
                schema.SortKey(a, row => row.Rank);
            }
            if (IsHierarchyMode(mode))
            {
                schema.Hierarchy(a, row => notifying ? row.Live : row.Plain, row => row.IsExpanded,
                    (row, value) => row.IsExpanded = value);
            }
            table.Width = 500;
            table.Height = 500;
            table.SortInterval = mode is "sorted-moves" or "pinned" or "hierarchy-moves" or "ties"
                ? TimeSpan.Zero : TimeSpan.FromSeconds(2);
            table.ItemsSource = source;
            await TableHarness.LoadAsync(table);
            return table;
        }

        private IEnumerable<Table> Tables => new[] { Incremental, Whole };

        /// <summary>Bring both tables into the mode by the same steps.</summary>
        internal void Setup()
        {
            if (_mode is not ("unsorted" or "held-unsorted"))
            {
                foreach (Table table in Tables)
                {
                    table.Sort = new(table.Columns[0], _mode == "pinned" ? SortDirection.Descending : SortDirection.Ascending);
                    // The view then shows the kept order's own version, so the change under test
                    // reaches the view as the kept order's stated difference, not by its walk.
                    table.RefreshView();
                }
            }
            foreach (Table table in Tables)
            {
                table.Selection = new Selection(new object[] { _rows[4] });
            }
            switch (_mode)
            {
                case "pinned":
                    // The pointed row's value rises past the others; it keeps its index while pointed.
                    EqRow pointed = _rows[7];
                    foreach (Table table in Tables)
                    {
                        Proof.Call(table, "PointAt", pointed);
                    }
                    pointed.Rank = 1000;
                    foreach (Table table in Tables)
                    {
                        table.RefreshView(new object[] { pointed });
                        Proof.Call(table, "StopPointing");
                    }
                    break;
                case "held" or "held-unsorted":
                    // The pointer rests on the row surface below the rows; a row that leaves is held.
                    foreach (Table table in Tables)
                    {
                        Proof.Call(table, "PointAt", new object?[] { null });
                    }
                    Change(list => list.RemoveAt(3), reset: false);
                    _heldIndex = Keys(Incremental).IndexOf("k3");
                    break;
                case "withheld":
                    _rows[5].Name = "zz";
                    foreach (Table table in Tables)
                    {
                        table.RefreshView(new object[] { _rows[5] });
                    }
                    break;
            }
        }

        /// <summary>The change under test: a notification to one table, a whole publish to the other.</summary>
        internal void Apply()
        {
            EqRow added = new("x", 25);
            switch (Kind)
            {
                case Kind.Add:
                    Change(list => list.Insert(2, added), reset: false);
                    break;
                case Kind.Remove:
                    Change(list => list.RemoveAt(2), reset: false);
                    break;
                case Kind.ReplaceSame:
                    EqRow old = _plain[2];
                    EqRow same = new(old.Key, -5);
                    Change(list => list[2] = same, reset: false);
                    break;
                case Kind.ReplaceNew:
                    EqRow other = new("y", 35);
                    Change(list => list[2] = other, reset: false);
                    break;
                case Kind.ReplaceThenAdd:
                    // A new instance with the same key, then an arrival just after it that ties
                    // with it: only source order places the two, so the new instance must keep
                    // its own source index, not the one after it.
                    EqRow kept = _plain[2];
                    EqRow again = new(kept.Key, kept.Rank);
                    Change(list => list[2] = again, reset: false);
                    EqRow tie = new("z", kept.Rank);
                    Change(list => list.Insert(3, tie), reset: false);
                    break;
                case Kind.Move:
                    _live.Move(1, 4);
                    EqRow moved = _plain[1];
                    _plain.RemoveAt(1);
                    _plain.Insert(4, moved);
                    PublishWhole();
                    break;
                case Kind.Reset:
                    Change(list => list.Insert(2, added), reset: true);
                    break;
                case Kind.Values:
                    EqRow changed = _plain[2];
                    changed.Rank = -5;
                    changed.Name = "0";
                    Incremental.RefreshView(new object[] { changed });
                    if (IsHierarchy)
                    {
                        PublishWhole();
                    }
                    else
                    {
                        Whole.RefreshView();
                    }
                    break;
                case Kind.Return:
                case Kind.ReturnThenPublish:
                    // Another rank and source index than the held row had: while the pointer
                    // stays, it still takes the held row's place.
                    EqRow returning = new("k3", 65);
                    Change(list => list.Insert(6, returning), reset: false);
                    RequireReturnedInPlace("the returning row");
                    if (Kind == Kind.ReturnThenPublish)
                    {
                        // The pointer still stays, so another row's change moves nothing either.
                        EqRow valued = _plain[1];
                        valued.Rank = 5;
                        Incremental.RefreshView(new object[] { valued });
                        PublishWhole();
                        RequireReturnedInPlace("after another change, the returned row");
                    }
                    break;
            }
        }

        /// <summary>
        /// Apply <paramref name="change"/> to the notifying list, quietly followed by a Reset when
        /// <paramref name="reset"/> is set, and to the plain list followed by a whole publish.
        /// </summary>
        private void Change(Action<IList<EqRow>> change, bool reset)
        {
            if (reset)
            {
                List<EqRow> copy = _live.ToList();
                change(copy);
                for (int index = 0; index < copy.Count; index++)
                {
                    if (index >= _live.Count || !ReferenceEquals(_live[index], copy[index]))
                    {
                        _live.InsertQuietly(index, copy[index]);
                    }
                }
                _live.RaiseReset();
            }
            else
            {
                change(_live);
            }
            change(_plain);
            PublishWhole();
        }

        private void RequireReturnedInPlace(string row)
        {
            foreach (Table table in Tables)
            {
                IList<string> keys = Keys(table);
                if (_heldIndex < 0 || _heldIndex >= keys.Count || keys[_heldIndex] != "k3")
                {
                    throw new InvalidOperationException(
                        $"{row} is not at the held row's index {_heldIndex}: {string.Join(",", keys)}");
                }
            }
        }

        private static IList<string> Keys(Table table) =>
            ((IEnumerable)SelectionHarness.Descendant<Microsoft.UI.Xaml.Controls.ListView>(table)!.ItemsSource)
                .Cast<EqRow>().Select(row => row.Key).ToList();

        /// <summary>
        /// A plain list changed in place reaches the table as a new assignment: section 5.3's
        /// <c>RefreshView()</c> re-reads only the source as last assigned or notified.
        /// </summary>
        private void PublishWhole()
        {
            Whole.ItemsSource = IsHierarchy ? _roots.ToArray() : _plain.ToList();
        }
    }
}
