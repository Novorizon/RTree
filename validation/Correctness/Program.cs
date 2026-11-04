using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RTree;

// No testing packages are required. Oracles below deliberately do not call the
// library's geometry methods. A nonzero exit code means a regression was found.
internal static class Program
{
    private static long assertions, mutations, queries, nodeChecks;
    private static int scenarios, regressions;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            Regression("recycled nodes and deletion reinsertion", RecycledNodes);
            Regression("root bounds, empty tree and refill", RootBounds);
            Regression("failed operations preserve mappings", FailedOperations);
            Regression("rectangle equality and hashing", Equality);
            Regression("rectangle subclass deletion uses bounds", RectangleSubclasses);
            Regression("invalid input is rejected", InvalidInput);
            Regression("three-dimensional geometry", ThreeDimensionalGeometry);
            Regression("nearest ties and inclusive distance limits", NearestSemantics);
            Regression("nearest rounded norms and inclusive sqrt limits", RoundedNearestLimits);
            Regression("finite extreme coordinates", ExtremeCoordinates);
            Regression("minimum subnormal nearest ordering", MinimumSubnormalNearest);
            Regression("item slots are recycled and references released", ItemSlots);
            Regression("complete drain and node reuse", DrainAndReuse);
            Regression("STR packing and rebuild preserve every item", BulkLoading);
            Regression("caller-owned query buffers and statistics", QueryBuffers);
            Regression("reused queries allocate no managed memory", AllocationFreeQueries);
            Regression("concurrent queries use independent scratch", ConcurrentQueries);
            Regression("queries and writes are synchronized", ConcurrentMutations);
            if (!args.Contains("--regressions-only")) RandomizedOperations();
            Console.WriteLine("PASS: {0} regression groups, {1} randomized scenarios, {2:N0} mutations, {3:N0} differential query comparisons, {4:N0} node invariant checks, {5:N0} assertions; {6:F2}s.",
                regressions, scenarios, mutations, queries, nodeChecks, assertions, elapsed.Elapsed.TotalSeconds);
            if (scenarios != 0) Console.WriteLine("Randomized inputs: capacities 2/3/4/10; distributions uniform/points/nested/Z-only/identical/planar/line; seeds 7100..7105. Every scenario ends with a shuffled full drain.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after {0:N0} mutations and {1:N0} query comparisons: {2}", mutations, queries, exception);
            return 1;
        }
    }

    private static void Regression(string name, Action test)
    {
        try { test(); regressions++; Console.WriteLine("PASS regression: " + name); }
        catch (Exception error) { throw new Exception("Regression: " + name, error); }
    }

    private static void Require(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception(message + ": expected " + typeof(T).Name);
    }

    private static T Field<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, Fields);
        if (field == null) throw new Exception("Missing diagnostic field " + name);
        return (T)field.GetValue(instance);
    }

    private static Rectangle Box(int i)
    {
        return new Rectangle(10.0 * i, 10.0 * i, 10.0 * i + 1, 10.0 * i + 1, 10.0 * i, 10.0 * i + 1);
    }

    private static Rectangle Cube(double low, double high)
    {
        return new Rectangle(low, low, high, high, low, high);
    }

    private static void Add(RTree<int> tree, Dictionary<int, Rectangle> expected, int item, Rectangle box)
    {
        tree.Add(box, item);
        expected.Add(item, box);
        mutations++;
        Validate(tree, expected);
    }

    private static void Delete(RTree<int> tree, Dictionary<int, Rectangle> expected, int item)
    {
        Require(tree.Delete(expected[item], item), "A live item could not be deleted: " + item);
        expected.Remove(item);
        mutations++;
        Validate(tree, expected);
    }

    private static void RecycledNodes()
    {
        var tree = new RTree<int>();
        var expected = new Dictionary<int, Rectangle>();
        for (int i = 0; i < 11; i++) Add(tree, expected, i, Box(i));
        Delete(tree, expected, 0);
        Add(tree, expected, 11, Box(11));
        Same(tree.Intersects(Cube(-1, 200)), expected.Keys, "short recycled-ID sequence");

        tree = new RTree<int>(); expected.Clear();
        for (int i = 0; i < 100; i++) Add(tree, expected, i, Box(i));
        foreach (int item in new[] { 59, 12, 0 }) Delete(tree, expected, item);
        Same(tree.Intersects(Cube(-1, 2000)), expected.Keys, "reinsertion must not lose unrelated item 3");
        Require(tree.Intersects(Box(3)).Contains(3), "Unrelated item 3 disappeared");
    }

    private static void RootBounds()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        Require(tree.getBounds() == null, "A new tree must have null bounds");
        Add(tree, expected, 0, Box(0)); Add(tree, expected, 1, Box(1));
        Delete(tree, expected, 0);
        Require(Equal(tree.getBounds(), Box(1)), "Root bounds were not tightened below minimum occupancy");
        var copy = tree.getBounds(); copy.min[0] = -1000;
        Require(Equal(tree.getBounds(), Box(1)), "getBounds leaked its mutable internal MBR");
        Delete(tree, expected, 1);
        Require(tree.getBounds() == null, "An empty tree retained bounds");
        Add(tree, expected, 5, Box(5));
        Require(Equal(tree.getBounds(), Box(5)), "Refilled tree retained a deleted boundary");
    }

    private static void FailedOperations()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        Throws<ArgumentNullException>(() => tree.Add(null, 7), "null rectangle");
        Validate(tree, expected);
        Add(tree, expected, 7, Box(7));
        for (int i = 0; i < 5; i++)
        {
            Throws<ArgumentException>(() => tree.Add(Box(8), 7), "duplicate item");
            Validate(tree, expected);
        }
        Require(!tree.Delete(Box(99), 99), "Delete of unknown item should return false");
        Require(!tree.Delete(Box(8), 7), "Delete using incorrect bounds should return false");
        Validate(tree, expected);
        Delete(tree, expected, 7);
        Require(!tree.Delete(Box(7), 7), "Repeated Delete should return false");
        Add(tree, expected, 7, Box(7));

        var references = new RTree<string>();
        Throws<ArgumentNullException>(() => references.Add(Box(0), null), "null item");
        Require(references.Count == 0, "null item modified count");
        Require(Field<Dictionary<string, int>>(references, "itemIds").Count == 0, "null item leaked an item mapping");
        Require(Field<bool[]>(references, "itemLive").All(value => !value), "null item occupied a slot");
        Require(Field<string[]>(references, "itemValues").All(value => value == null), "null item retained an item reference");
    }

    private static void Equality()
    {
        var a = new Rectangle(0, 0, 1, 1, 0, 1); var b = new Rectangle(0, 0, 1, 1, 0, 1);
        Require(a.Equals(b), "Equal rectangles differ");
        Require(!a.Equals(null), "Rectangle.Equals(null) must be false");
        Require(!a.Equals("box"), "Rectangle equals an unrelated type");
        Require(a.GetHashCode() == b.GetHashCode(), "Equal rectangles have different hashes");
        Require(new HashSet<Rectangle> { a }.Contains(b), "Equal rectangle not found in HashSet");
        var zero = new Rectangle(-0.0, -0.0, -0.0, -0.0, -0.0, -0.0);
        var plus = new Rectangle(0, 0, 0, 0, 0, 0);
        Require(zero.Equals(plus) && zero.GetHashCode() == plus.GetHashCode(), "Signed zero violates equality/hash contract");
        var tree = new RTree<Rectangle>(); tree.Add(a, a);
        Require(tree.Delete(a, b) && tree.Count == 0, "Equal rectangle item could not be deleted");
    }

    private static void InvalidInput()
    {
        Throws<ArgumentException>(() => new Rectangle(double.NaN, 0, 1, 1, 0, 1), "NaN rectangle");
        Throws<ArgumentException>(() => new Rectangle(0, 0, double.PositiveInfinity, 1, 0, 1), "infinite rectangle");
        Throws<ArgumentNullException>(() => new Rectangle(null, new double[3]), "null min array");
        Throws<ArgumentNullException>(() => new Rectangle(new double[3], null), "null max array");
        Throws<ArgumentException>(() => new Rectangle(new double[2], new double[3]), "wrong array dimensions");
        Throws<ArgumentException>(() => new Rectangle(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 1 }), "inverted bounds");
        Throws<ArgumentException>(() => new Rectangle(new[] { 0.0, double.NaN, 0 }, new[] { 1.0, 1, 1 }), "NaN array bounds");
        Throws<ArgumentException>(() => new Point(double.NaN, 0, 0), "NaN point");
        Throws<ArgumentException>(() => new Point(0, double.NegativeInfinity, 0), "infinite point");
        var tree = new RTree<int>();
        Throws<ArgumentNullException>(() => tree.Intersects(null), "null intersection query");
        Throws<ArgumentNullException>(() => tree.Contains(null), "null containment query");
        Throws<ArgumentNullException>(() => tree.Nearest(null, 1), "null nearest point");
        Throws<ArgumentNullException>(() => tree.Delete(null, 1), "null deletion rectangle");
        Throws<ArgumentOutOfRangeException>(() => tree.Nearest(new Point(0, 0, 0), -1), "negative nearest distance");
        Throws<ArgumentOutOfRangeException>(() => tree.Nearest(new Point(0, 0, 0), double.NaN), "NaN nearest distance");
        Require(tree.Nearest(new Point(0, 0, 0), double.PositiveInfinity).Count == 0, "unbounded query on empty tree");
    }

    private static void RectangleSubclasses()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        // Add defensively copies a Rectangle into a base-class instance. The
        // caller's derived type must not prevent deleting the same bounds.
        Add(tree, expected, 0, new DerivedRectangle(0, 1));
        Delete(tree, expected, 0);

        Add(tree, expected, 1, Cube(2, 3));
        Require(tree.Delete(new DerivedRectangle(2, 3), 1), "Derived query bounds did not match a base-class entry");
        expected.Remove(1); mutations++; Validate(tree, expected);

        Add(tree, expected, 2, new DerivedRectangle(4, 5));
        Require(tree.Delete(Cube(4, 5), 2), "Base query bounds did not match a derived rectangle's copied entry");
        expected.Remove(2); mutations++; Validate(tree, expected);

        Add(tree, expected, 3, new FalseEqualityRectangle(6, 7));
        Delete(tree, expected, 3);
        Add(tree, expected, 4, Cube(8, 9));
        Require(tree.Delete(new FalseEqualityRectangle(8, 9), 4), "Geometric deletion incorrectly invoked virtual Equals");
        expected.Remove(4); mutations++; Validate(tree, expected);

        Add(tree, expected, 5, Cube(10, 11));
        Require(!tree.Delete(new FalseEqualityRectangle(10, 12), 5), "Different derived bounds incorrectly matched");
        Validate(tree, expected);
    }

    private sealed class DerivedRectangle : Rectangle
    {
        internal DerivedRectangle(double low, double high) : base(low, low, high, high, low, high) { }
    }

    private sealed class FalseEqualityRectangle : Rectangle
    {
        internal FalseEqualityRectangle(double low, double high) : base(low, low, high, high, low, high) { }
        public override bool Equals(object other) { return false; }
        public override int GetHashCode() { return 0; }
    }

    private static void ThreeDimensionalGeometry()
    {
        var a = new Rectangle(0, 0, 2, 3, 0, 4);
        Require(a.area() == 24, "Three-dimensional volume must include Z");
        var cube = Cube(0, 1); var distant = new Rectangle(0, 0, 1, 1, 1000, 1001);
        Require(cube.enlargement(distant) == 1000, "Z-only enlargement was ignored");
        var lo = new[] { 0.0, 0, 0 }; var hi = new[] { 1.0, 1, 1 }; var copy = new Rectangle(lo, hi);
        lo[0] = 100; hi[0] = 200;
        Require(Equal(copy, cube), "Array constructor retained caller-owned arrays");
        var tree = new RTree<int>(); tree.Add(cube, 0); tree.Add(distant, 1);
        Same(tree.Intersects(new Rectangle(0, 0, 1, 1, 500, 501)), new int[0], "Z-only nonintersection");
        Same(tree.Intersects(new Rectangle(1, 1, 2, 2, 1, 2)), new[] { 0 }, "touching corners intersect inclusively");
        Same(tree.Contains(cube), new[] { 0 }, "containment includes equal boundaries");
    }

    private static void NearestSemantics()
    {
        var tree = new RTree<int>();
        tree.Add(new Rectangle(-1, 0, -1, 0, 0, 0), 0);
        tree.Add(new Rectangle(1, 0, 1, 0, 0, 0), 1);
        tree.Add(new Rectangle(3, 0, 3, 0, 0, 0), 2);
        var point = new Point(0, 0, 0);
        Same(tree.Nearest(p: point, furthestDistance: 10), new[] { 0, 1 }, "legacy named arguments and all nearest ties");
        Same(tree.Nearest(point, 1), new[] { 0, 1 }, "inclusive radius");
        Same(tree.Nearest(point, .99), new int[0], "nothing within distance limit");
        Same(tree.Nearest(point, double.PositiveInfinity), new[] { 0, 1 }, "unbounded nearest");
        tree.Add(Cube(0, 0), 3);
        Same(tree.Nearest(point, 0), new[] { 3 }, "zero distance limit");
        Same(tree.Nearest(new Point(100, 0, 0), 100), new[] { 2 }, "repeated query cannot retain prior ties");
    }

    private static Rectangle PointBox(double x, double y, double z)
    {
        return new Rectangle(x, y, x, y, z, z);
    }

    private static void RoundedNearestLimits()
    {
        var origin = new Point(0, 0, 0);
        var coordinates = new List<int[]> { new[] { 1, 1, 1 }, new[] { 1, 1, 0 }, new[] { 1, 2, 0 }, new[] { 1, 1, 2 }, new[] { 1, 2, 2 }, new[] { 2, 3, 4 } };
        var random = new Random(15871);
        for (int i = 0; i < 48; i++) coordinates.Add(new[] { random.Next(1, 21), random.Next(1, 21), random.Next(1, 21) });
        foreach (int capacity in new[] { 2, 10 })
        foreach (int[] coordinate in coordinates)
        {
            double x = coordinate[0], y = coordinate[1], z = coordinate[2];
            double radius = Math.Sqrt(x * x + y * y + z * z);
            var tree = new RTree<int>(capacity, capacity / 2);
            tree.Add(PointBox(x, y, z), 0);
            if (capacity == 2)
            {
                for (int i = 1; i <= 4; i++) tree.Add(PointBox(1000 * i, 1000 * i, 1000 * i), i);
                Require(Field<TreeNode[]>(tree, "nodes")[tree.getRootNodeId()].Level > 1, "Sqrt-radius fixture did not exercise internal pruning");
            }
            Same(tree.Nearest(origin, radius), new[] { 0 }, "sqrt-of-squared-distance radius must be inclusive");
            double below = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(radius) - 1);
            Same(tree.Nearest(origin, below), new int[0], "one-ULP-smaller radius must exclude the rounded norm");
        }

        // Two different squared sums may round to exactly the same public
        // double-valued distance. Preserve the original all-ties semantics.
        double belowOne = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) - 2);
        Require(1 + 1 + belowOne * belowOne < 3 && Math.Sqrt(1 + 1 + belowOne * belowOne) == Math.Sqrt(3), "Rounded-norm tie fixture is not tied");
        foreach (int capacity in new[] { 2, 10 })
        {
            var tree = new RTree<int>(capacity, capacity / 2);
            tree.Add(PointBox(1, 1, 1), 0);
            tree.Add(PointBox(1, 1, belowOne), 1);
            if (capacity == 2) for (int i = 2; i <= 7; i++) tree.Add(PointBox(i * 100, i * 100, i * 100), i);
            Same(tree.Nearest(origin, double.PositiveInfinity), new[] { 0, 1 }, "same rounded norm must retain both nearest ties");
            Same(tree.Nearest(origin, Math.Sqrt(3)), new[] { 0, 1 }, "rounded nearest ties at inclusive radius");

            var outside = new RTree<int>(capacity, capacity / 2);
            outside.Add(PointBox(1.5, 1.5e-8, 0), 0);
            if (capacity == 2) for (int i = 1; i <= 4; i++) outside.Add(PointBox(i * 100, i * 100, 0), i);
            Require(Math.Sqrt(1.5 * 1.5 + 1.5e-8 * 1.5e-8) > 1.5, "Above-radius fixture did not exceed its limit");
            Same(outside.Nearest(origin, 1.5), new int[0], "epsilon slack must not admit a norm above the radius");
        }
    }

    private static void ExtremeCoordinates()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        Add(tree, expected, 0, new Rectangle(1e200, 0, 1e200, 0, 0, 0));
        Add(tree, expected, 1, new Rectangle(2e200, 0, 2e200, 0, 0, 0));
        Same(tree.Nearest(new Point(0, 0, 0), double.PositiveInfinity), new[] { 0 }, "overflow-safe point distance");
        Same(tree.Nearest(new Point(0, 0, 0), 1.5e200), new[] { 0 }, "large finite limit");
        Same(tree.Nearest(new Point(0, 0, 0), .5e200), new int[0], "large excluded distance");

        tree = new RTree<int>(2, 1); expected.Clear();
        for (int i = 0; i < 30; i++)
        {
            double x = (i - 15) * 1e300;
            Add(tree, expected, i, new Rectangle(x, x, x + 1e299, x + 1e299, x, x + 1e299));
        }
        Same(tree.Intersects(Cube(-1e299, 1e299)), expected.Where(kv => Intersects(kv.Value, Cube(-1e299, 1e299))).Select(kv => kv.Key), "overflowing volume split");
        CompareNearest(tree, expected, new Point(0, 0, 0), double.PositiveInfinity, "overflowing volume nearest");
        foreach (int item in expected.Keys.OrderBy(i => (i * 17) % 31).ToArray()) Delete(tree, expected, item);

        var span = new Rectangle(new[] { -double.MaxValue, 0.0, 0.0 }, new[] { double.MaxValue, 0.0, 0.0 });
        Require(!double.IsNaN(span.area()), "Zero-width extreme volume produced NaN");
        Require(!double.IsNaN(span.enlargement(Cube(-1, 1))), "Extreme enlargement produced NaN");
        var tiny = new Rectangle(1e-200, 0, 1e-200, 0, 0, 0);
        Require(tiny.distance(new Point(0, 0, 0)) > 0, "Tiny nonzero distance underflowed to zero");
        foreach (var distances in new[] { new[] { 1.5e-162, 2e-162 }, new[] { 2e-162, 2.1e-162 }, new[] { 1e-200, 2e-200 } })
        {
            tree = new RTree<int>();
            tree.Add(new Rectangle(distances[1], 0, distances[1], 0, 0, 0), 1);
            tree.Add(new Rectangle(distances[0], 0, distances[0], 0, 0, 0), 0);
            Same(tree.Nearest(new Point(0, 0, 0), double.PositiveInfinity), new[] { 0 }, "distinct tiny distances must not become tied");
            Same(tree.Nearest(new Point(0, 0, 0), distances[0]), new[] { 0 }, "inclusive tiny limit");
            Same(tree.Nearest(new Point(0, 0, 0), distances[0] / 2), new int[0], "tiny excluded distance");
        }

        // Both actual distances exceed double.MaxValue. A scalar infinity
        // result cannot distinguish them, so assert the mathematically nearer
        // item directly instead of using the ordinary finite-distance oracle.
        double largest = double.MaxValue;
        tree = new RTree<int>(2, 1); expected.Clear();
        Add(tree, expected, 1, new Rectangle(largest, 0, largest, 0, 0, 0));
        Add(tree, expected, 0, new Rectangle(largest / 2, 0, largest / 2, 0, 0, 0));
        Add(tree, expected, 2, new Rectangle(largest / 2, 0, largest / 2, 0, 0, 0));
        Same(tree.Nearest(new Point(-largest, 0, 0), double.PositiveInfinity), new[] { 0, 2 }, "overrange coordinate gap ordering and ties");
        Same(tree.Nearest(new Point(-largest, 0, 0), largest), new int[0], "finite limit excludes every overrange distance");

        tree = new RTree<int>(2, 1); expected.Clear();
        for (int i = 29; i >= 0; i--)
        {
            double x = largest * (.25 + .02 * i);
            Add(tree, expected, i, new Rectangle(x, 0, x, 0, 0, 0));
        }
        Same(tree.Nearest(new Point(-largest, 0, 0), double.PositiveInfinity), new[] { 0 }, "internal overrange MBR pruning retains nearest");

        tree = new RTree<int>(2, 1); expected.Clear();
        Add(tree, expected, 1, new Rectangle(largest * .9, largest * .9, largest * .9, largest * .9, 0, 0));
        Add(tree, expected, 0, new Rectangle(largest * .8, largest * .8, largest * .8, largest * .8, 0, 0));
        Same(tree.Nearest(new Point(0, 0, 0), double.PositiveInfinity), new[] { 0 }, "multi-axis overrange norm ordering");
        Same(tree.Nearest(new Point(0, 0, 0), largest), new int[0], "finite radius excludes multi-axis overrange norms");
        Add(tree, expected, 2, new Rectangle(largest * .6, largest * .6, largest * .6, largest * .6, 0, 0));
        Same(tree.Nearest(new Point(0, 0, 0), largest), new[] { 2 }, "finite rank beats overrange rank");
    }

    private static void ItemSlots()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        for (int i = 0; i < 128; i++) Add(tree, expected, i, Box(i));
        int slotsBefore = Field<int>(tree, "nextItemId");
        var ids = Field<Dictionary<int, int>>(tree, "itemIds");
        int released = ids[1];
        Delete(tree, expected, 1);
        Require(!Field<bool[]>(tree, "itemLive")[released], "Deleted slot remains live");
        Add(tree, expected, 1000, Box(1000));
        Require(ids[1000] == released, "Released item slot was not reused");
        Require(Field<int>(tree, "nextItemId") == slotsBefore, "Reusing an item grew the slot high-water mark");
        for (int cycle = 0; cycle < 30; cycle++)
        {
            Delete(tree, expected, 1000 + cycle);
            Add(tree, expected, 1001 + cycle, Box(1001 + cycle));
        }
        Require(Field<int>(tree, "nextItemId") == slotsBefore, "Bounded item churn kept growing the item pool");
        Same(tree.Intersects(Cube(-1, 20000)), expected.Keys, "item queries remain correct after slot reuse");

        var references = new RTree<object>();
        var item = new object(); references.Add(Box(0), item);
        int slot = Field<Dictionary<object, int>>(references, "itemIds")[item];
        Require(references.Delete(Box(0), item), "Reference item deletion failed");
        Require(Field<object[]>(references, "itemValues")[slot] == null, "Released slot retains the user's object");
        Require(Field<Dictionary<object, int>>(references, "itemIds").Count == 0, "Deleted reference remains in item lookup");
    }

    private static void MinimumSubnormalNearest()
    {
        double e = double.Epsilon;
        var origin = new Point(0, 0, 0);
        var tree = new RTree<int>();
        tree.Add(new Rectangle(e, e, e, e, 0, 0), 1);
        tree.Add(new Rectangle(e, 0, e, 0, 0, 0), 0);
        Same(tree.Nearest(origin, double.PositiveInfinity), new[] { 0 }, "minimum subnormal axis is closer than diagonal");
        Same(tree.Nearest(origin, e), new[] { 0 }, "minimum positive radius excludes diagonal");
        Same(tree.Nearest(origin, 0), new int[0], "zero radius excludes minimum positive distance");
        tree.Add(Cube(0, 0), 2);
        Same(tree.Nearest(origin, 0), new[] { 2 }, "exact zero wins over minimum subnormal distance");
        Same(tree.Nearest(origin, double.PositiveInfinity), new[] { 2 }, "unbounded exact-zero nearest");

        tree = new RTree<int>(2, 1); var expected = new Dictionary<int, Rectangle>();
        Add(tree, expected, 1, new Rectangle(e, e, e, e, 0, 0));
        Add(tree, expected, 2, new Rectangle(-e, e, -e, e, 0, 0));
        Add(tree, expected, 3, new Rectangle(e, -e, e, -e, 0, 0));
        Add(tree, expected, 4, new Rectangle(-e, -e, -e, -e, 0, 0));
        Add(tree, expected, 0, new Rectangle(e, 0, e, 0, 0, 0));
        Add(tree, expected, 5, new Rectangle(-e, 0, -e, 0, 0, 0));
        Add(tree, expected, 6, new Rectangle(2 * e, 0, 2 * e, 0, 0, 0));
        Require(Field<TreeNode[]>(tree, "nodes")[tree.getRootNodeId()].Level > 1, "Minimum-subnormal pruning fixture did not create internal nodes");
        Same(tree.Nearest(origin, double.PositiveInfinity), new[] { 0, 5 }, "internal pruning distinguishes minimum subnormal norms");
        Same(tree.Nearest(origin, e), new[] { 0, 5 }, "internal pruning preserves inclusive minimum positive limit");
        Same(tree.Nearest(origin, 0), new int[0], "internal pruning rejects nonzero distances at zero radius");
        Same(tree.Nearest(new Point(e, e, 0), 0), new[] { 1 }, "zero-distance match survives internal subnormal pruning");
        Delete(tree, expected, 0); Delete(tree, expected, 5);
        Same(tree.Nearest(origin, double.PositiveInfinity), new[] { 1, 2, 3, 4 }, "equal minimum subnormal diagonals remain tied");
        Same(tree.Nearest(origin, e), new int[0], "rounded diagonal norm must not fit minimum axis radius");
    }

    private static void DrainAndReuse()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>(); var random = new Random(123456);
        for (int cycle = 0; cycle < 12; cycle++)
        {
            for (int i = 0; i < 180; i++) Add(tree, expected, cycle * 180 + i, RandomBox(random, cycle % 7, i));
            var pending = expected.Keys.ToList(); Shuffle(pending, random);
            foreach (int item in pending) Delete(tree, expected, item);
            Require(Field<TreeNode[]>(tree, "nodes").Count(node => node.Live) == 1, "Empty tree retained unreachable nodes");
        }
    }

    private static void BulkLoading()
    {
        foreach (int capacity in new[] { 2, 3, 4, 8, 16, 32, 64 })
        foreach (int size in new[] { 0, 1, capacity - 1, capacity, capacity + 1, 2 * capacity - 1, capacity * capacity + 1 }.Distinct())
        {
            var random = new Random(8191 + capacity * 31 + size);
            var expected = new Dictionary<int, Rectangle>();
            for (int i = 0; i < size; i++) expected.Add(i, RandomBox(random, (i + size) % 7, i));
            var source = expected.Select(pair => new KeyValuePair<Rectangle, int>(pair.Value, pair.Key)).ToArray();
            var tree = RTree<int>.BulkLoad(source, capacity, capacity / 2);
            Validate(tree, expected);
            CompareQueries(tree, expected, random);
            Same(tree.Intersects(Cube(-1000, 1000)), expected.Keys, "STR retains all inputs at packing boundaries");
            for (int i = 0; i < Math.Min(12, size); i++) Delete(tree, expected, i);
            for (int i = size; i < size + 12; i++) Add(tree, expected, i, RandomBox(random, 0, i));
            tree.Rebuild();
            Validate(tree, expected);
            CompareQueries(tree, expected, random);
            foreach (int item in expected.Keys.Take(3).ToArray()) Delete(tree, expected, item);
            tree.Rebuild(); tree.Rebuild();
            Validate(tree, expected);
        }
        var empty = RTree<int>.BulkLoad(new KeyValuePair<Rectangle, int>[0]);
        Require(empty.Count == 0 && empty.getBounds() == null, "Default STR overload did not create an empty tree");
        empty.Rebuild(); Require(empty.getBounds() == null, "Empty Rebuild retained bounds");
        Throws<ArgumentNullException>(() => RTree<int>.BulkLoad(null), "null bulk sequence");
        Throws<ArgumentNullException>(() => RTree<int>.BulkLoad(new[] { new KeyValuePair<Rectangle, int>(null, 0) }), "null bulk rectangle");
        Throws<ArgumentNullException>(() => RTree<string>.BulkLoad(new[] { new KeyValuePair<Rectangle, string>(Box(0), null) }), "null bulk item");
        Throws<ArgumentException>(() => RTree<int>.BulkLoad(new[] { new KeyValuePair<Rectangle, int>(Box(0), 0), new KeyValuePair<Rectangle, int>(Box(1), 0) }), "duplicate bulk items");

        var subclass = RTree<int>.BulkLoad(new[] { new KeyValuePair<Rectangle, int>(new FalseEqualityRectangle(0, 1), 0) });
        Require(subclass.Delete(Cube(0, 1), 0), "Packed derived rectangle could not be deleted by bounds");
        var huge = RTree<int>.BulkLoad(new[] {
            new KeyValuePair<Rectangle, int>(new Rectangle(double.MaxValue / 2, 0, double.MaxValue / 2, 0, 0, 0), 0),
            new KeyValuePair<Rectangle, int>(new Rectangle(double.MaxValue, 0, double.MaxValue, 0, 0, 0), 1)
        }, 2, 1);
        Same(huge.Nearest(new Point(-double.MaxValue, 0, 0), double.PositiveInfinity), new[] { 0 }, "packed extreme centers preserve nearest ordering");

        // Sorting by an invariant axis must not replace an already spatially
        // sorted order with arbitrary IDs. This fixture caught Z-only STR
        // grouping being scrambled by constant X/Y center comparisons.
        var order = Enumerable.Range(0, 1000).ToList(); Shuffle(order, new Random(6841));
        var line = RTree<int>.BulkLoad(order.Select(i => new KeyValuePair<Rectangle, int>(new Rectangle(0, 0, 1, 1, i, i + .1), i)), 10, 5);
        var lineNodes = Field<TreeNode[]>(line, "nodes");
        foreach (var node in lineNodes.Where(node => node.Live && node.Level == 1))
            Require(node.Bounds.MaxZ - node.Bounds.MinZ <= 9.100001, "Z-only STR leaf lost spatial adjacency to a constant axis");
        var localContext = new QueryContext(true); var localResults = new List<int>(10);
        line.Intersects(new Rectangle(-1, -1, 2, 2, 400, 400.1), localResults, localContext);
        Same(localResults, new[] { 400 }, "local Z-only packed query");
        Require(localContext.NodesVisited < lineNodes.Count(node => node.Live) / 4, "Local Z-only query visited a linear fraction of the packed tree");
    }

    private static void QueryBuffers()
    {
        var random = new Random(36281);
        var expected = new Dictionary<int, Rectangle>();
        for (int i = 0; i < 300; i++) expected.Add(i, RandomBox(random, i % 7, i));
        var tree = RTree<int>.BulkLoad(expected.Select(pair => new KeyValuePair<Rectangle, int>(pair.Value, pair.Key)), 16, 8);
        var buffer = new List<int>(expected.Count) { -1 };
        var context = new QueryContext(true);
        var range = Cube(-20, 40);
        int count = tree.Intersects(range, buffer, context);
        Require(count == buffer.Count && context.ResultCount == count, "Intersection result count/statistics differ");
        Require(context.NodesVisited > 0 && context.EntriesTested > 0, "Selective query did not record traversal work");
        Same(buffer, expected.Where(pair => Intersects(pair.Value, range)).Select(pair => pair.Key), "caller-owned intersection buffer");
        count = tree.Contains(range, buffer, context);
        Require(count == buffer.Count && context.ResultCount == count, "Containment result count/statistics differ");
        Same(buffer, expected.Where(pair => Contains(range, pair.Value)).Select(pair => pair.Key), "caller-owned containment buffer");
        var point = new Point(7, -3, 4);
        count = tree.Nearest(point, double.PositiveInfinity, buffer, context);
        Require(count == buffer.Count && context.ResultCount == count, "Nearest result count/statistics differ");
        double nearest = expected.Min(pair => Distance(pair.Value, point));
        Same(buffer, expected.Where(pair => Distance(pair.Value, point) == nearest).Select(pair => pair.Key), "caller-owned nearest buffer");
        Require(tree.Intersects(Cube(1000, 1001), buffer, context) == 0 && buffer.Count == 0 && context.ResultCount == 0, "Buffer/context retained a previous query's results");

        var other = new RTree<int>(); other.Add(Box(0), 99);
        Require(other.Intersects(Box(0), buffer, context) == 1 && buffer[0] == 99, "A context could not be reused across trees");
        Require(other.Contains(Box(0), buffer) == 1 && buffer[0] == 99, "Default-context containment buffer overload");
        Require(other.Nearest(new Point(0, 0, 0), 0, buffer) == 1 && buffer[0] == 99, "Default-context nearest buffer overload");
        Require(other.Intersects(Box(0), buffer) == 1 && buffer[0] == 99, "Default-context intersection buffer overload");
        var disabled = new QueryContext();
        Require(!disabled.CollectStatistics && context.CollectStatistics, "Query statistics mode was not retained");
        Require(other.Intersects(Box(0), buffer, disabled) == 1, "Disabled-statistics query failed");
        Require(disabled.NodesVisited == 0 && disabled.EntriesTested == 0, "Disabled statistics still recorded traversal work");

        Throws<ArgumentNullException>(() => tree.Intersects(range, null, context), "null intersection result buffer");
        Throws<ArgumentNullException>(() => tree.Contains(range, buffer, null), "null containment context");
        Throws<ArgumentNullException>(() => tree.Nearest(point, 1, null, context), "null nearest result buffer");
        Throws<ArgumentNullException>(() => tree.Intersects(null, buffer, context), "null buffered query rectangle");
        Throws<ArgumentNullException>(() => tree.Nearest(null, 1, buffer, context), "null buffered query point");
        Throws<ArgumentOutOfRangeException>(() => tree.Nearest(point, double.NaN, buffer, context), "invalid buffered nearest limit");
        // Hold the workspace as another active query would. Reuse must fail
        // before touching its scratch or the caller's previous result list.
        context.Enter();
        try { Throws<InvalidOperationException>(() => other.Intersects(Box(0), buffer, context), "active query context reuse"); }
        finally { context.Exit(); }
        Require(other.Intersects(Box(0), buffer, context) == 1, "Context did not recover after rejected concurrent reuse");
        Same(tree.Intersects(range), expected.Where(pair => Intersects(pair.Value, range)).Select(pair => pair.Key), "legacy wrapper after failed buffer calls");
    }

    private static void AllocationFreeQueries()
    {
        var random = new Random(73571);
        var source = Enumerable.Range(0, 1024).Select(i => new KeyValuePair<Rectangle, int>(RandomBox(random, 0, i), i)).ToArray();
        var tree = RTree<int>.BulkLoad(source, 16, 8);
        var results = new List<int>(source.Length);
        var context = new QueryContext();
        var all = Cube(-1000, 1000); var selective = Cube(-10, 30); var point = new Point(101, 102, 103);
        for (int i = 0; i < 256; i++)
        {
            tree.Intersects(all, results, context); tree.Intersects(selective, results, context);
            tree.Contains(all, results, context); tree.Contains(selective, results, context);
            tree.Nearest(point, double.PositiveInfinity, results, context);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int resultCount = 0;
        for (int i = 0; i < 256; i++)
        {
            resultCount += tree.Intersects(all, results, context); resultCount += tree.Intersects(selective, results, context);
            resultCount += tree.Contains(all, results, context); resultCount += tree.Contains(selective, results, context);
            resultCount += tree.Nearest(point, double.PositiveInfinity, results, context);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(resultCount > 0, "Allocation test did not execute queries");
        Require(allocated == 0, "Warm buffer/context queries allocated " + allocated + " bytes");
    }

    private static void ConcurrentSame(IEnumerable<int> actual, int[] expected, string message)
    {
        var sorted = actual.OrderBy(value => value).ToArray();
        if (!sorted.SequenceEqual(expected)) throw new Exception(message);
        Interlocked.Increment(ref queries);
    }

    private static void ConcurrentQueries()
    {
        var random = new Random(17117);
        var expected = Enumerable.Range(0, 600).ToDictionary(i => i, i => RandomBox(random, i % 7, i));
        var tree = RTree<int>.BulkLoad(expected.Select(pair => new KeyValuePair<Rectangle, int>(pair.Value, pair.Key)), 16, 8);
        var ranges = Enumerable.Range(0, 8).Select(i => i == 0 ? Cube(-1000, 1000) : RandomBox(random, 0, i)).ToArray();
        var points = Enumerable.Range(0, 8).Select(i => new Point(random.Next(-150, 151), random.Next(-150, 151), random.Next(-150, 151))).ToArray();
        var intersections = ranges.Select(range => expected.Where(pair => Intersects(pair.Value, range)).Select(pair => pair.Key).OrderBy(i => i).ToArray()).ToArray();
        var containments = ranges.Select(range => expected.Where(pair => Contains(range, pair.Value)).Select(pair => pair.Key).OrderBy(i => i).ToArray()).ToArray();
        var nearest = points.Select(point => { double d = expected.Min(pair => Distance(pair.Value, point)); return expected.Where(pair => Distance(pair.Value, point) == d).Select(pair => pair.Key).OrderBy(i => i).ToArray(); }).ToArray();
        using (var start = new Barrier(8))
        {
            var workers = Enumerable.Range(0, 8).Select(worker => Task.Factory.StartNew(() =>
            {
                var context = new QueryContext(true); var result = new List<int>(expected.Count);
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new Exception("Concurrent reader start timed out");
                for (int i = 0; i < 80; i++)
                {
                    int q = (i + worker) % ranges.Length;
                    ConcurrentSame(tree.Intersects(ranges[q]), intersections[q], "Concurrent default intersection query");
                    tree.Intersects(ranges[q], result, context); ConcurrentSame(result, intersections[q], "Concurrent buffered intersection query");
                    ConcurrentSame(tree.Contains(ranges[q]), containments[q], "Concurrent default containment query");
                    tree.Contains(ranges[q], result, context); ConcurrentSame(result, containments[q], "Concurrent buffered containment query");
                    ConcurrentSame(tree.Nearest(points[q], double.PositiveInfinity), nearest[q], "Concurrent default nearest query");
                    tree.Nearest(points[q], double.PositiveInfinity, result, context); ConcurrentSame(result, nearest[q], "Concurrent buffered nearest query");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Require(Task.WaitAll(workers, TimeSpan.FromSeconds(30)), "Concurrent read queries deadlocked");
        }
        Validate(tree, expected);
    }

    private static void ConcurrentMutations()
    {
        var tree = new RTree<int>();
        for (int i = 0; i < 200; i++) tree.Add(Box(i), i);
        var range = Cube(-1, 20000);
        using (var start = new Barrier(5))
        {
            var readers = Enumerable.Range(0, 4).Select(worker => Task.Factory.StartNew(() =>
            {
                var result = new List<int>(201); var context = new QueryContext();
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new Exception("Concurrent mutation reader start timed out");
                for (int i = 0; i < 180; i++)
                {
                    if (i % 2 == 0) tree.Intersects(range, result, context); else tree.Contains(range, result, context);
                    if (result.Count != 200 && result.Count != 201) throw new Exception("Reader saw a partially applied write");
                    result.Sort();
                    for (int item = 0; item < 200; item++) if (result[item] != item) throw new Exception("Reader lost a permanent item during a write");
                    if (result.Count == 201 && (result[200] < 1000 || result[200] >= 1080)) throw new Exception("Reader saw an invalid transient item");
                    tree.Nearest(new Point(0, 0, 0), 0, result, context);
                    if (result.Count != 1 || result[0] != 0) throw new Exception("Concurrent nearest saw inconsistent state");
                    Interlocked.Add(ref queries, 2);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();
            readers.Add(Task.Factory.StartNew(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new Exception("Concurrent writer start timed out");
                for (int i = 0; i < 80; i++)
                {
                    tree.Add(Box(1000 + i), 1000 + i);
                    if (i % 8 == 0) tree.Rebuild();
                    if (!tree.Delete(Box(1000 + i), 1000 + i)) throw new Exception("Concurrent writer failed to delete its own item");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            Require(Task.WaitAll(readers.ToArray(), TimeSpan.FromSeconds(30)), "Concurrent reads/writes deadlocked");
        }
        Validate(tree, Enumerable.Range(0, 200).ToDictionary(i => i, Box));
    }

    private static void RandomizedOperations()
    {
        foreach (int capacity in new[] { 2, 3, 4, 10 })
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 5, 6 })
        foreach (int seed in new[] { 7100, 7101, 7102, 7103, 7104, 7105 })
        {
            scenarios++;
            try
            {
                var random = new Random(seed); var tree = new RTree<int>(capacity, capacity / 2);
                var expected = new Dictionary<int, Rectangle>(); int nextItem = 0;
                CompareQueries(tree, expected, random);
                for (int i = 0; i < 80; i++) { int item = nextItem++; Add(tree, expected, item, RandomBox(random, mode, item)); }
                for (int action = 0; action < 600; action++)
                {
                    if (expected.Count == 0 || random.Next(100) < 53)
                    {
                        int item = nextItem++; Add(tree, expected, item, RandomBox(random, mode, item));
                    }
                    else Delete(tree, expected, expected.Keys.ElementAt(random.Next(expected.Count)));
                    if (action % 10 == 0) CompareQueries(tree, expected, random);
                    if (action % 37 == 0)
                    {
                        int unknown = nextItem + 1000;
                        Require(!tree.Delete(RandomBox(random, 0, 0), unknown), "Deleting an unknown randomized item succeeded");
                        Validate(tree, expected);
                    }
                }
                var drain = expected.Keys.ToList(); Shuffle(drain, random);
                foreach (int item in drain) Delete(tree, expected, item);
                CompareQueries(tree, expected, random);
            }
            catch (Exception error)
            {
                throw new Exception(string.Format("Randomized scenario capacity={0} distribution={1} seed={2}", capacity, mode, seed), error);
            }
        }
    }

    private static Rectangle RandomBox(Random random, int mode, int item)
    {
        var min = new double[3]; var max = new double[3];
        for (int d = 0; d < 3; d++)
        {
            min[d] = random.Next(-100, 101); max[d] = min[d] + random.Next(0, 20);
            if (mode == 1) max[d] = min[d];
            if (mode == 2) { min[d] = -(item % 100); max[d] = item % 100; }
            if (mode == 3 && d < 2) { min[d] = 0; max[d] = 1; }
            if (mode == 4) { min[d] = -1; max[d] = 1; }
            if (mode == 5 && d == 2) { min[d] = 0; max[d] = 0; }
            if (mode == 6 && d >= 1) { min[d] = 0; max[d] = 0; }
        }
        return new Rectangle(min, max);
    }

    private static void Shuffle(List<int> values, Random random)
    {
        for (int i = values.Count - 1; i > 0; i--) { int j = random.Next(i + 1); int value = values[i]; values[i] = values[j]; values[j] = value; }
    }

    private static void CompareQueries(RTree<int> tree, Dictionary<int, Rectangle> expected, Random random)
    {
        for (int q = 0; q < 3; q++)
        {
            var query = q == 0 ? Cube(-1000, 1000) : RandomBox(random, 0, 0);
            Same(tree.Intersects(query), expected.Where(kv => Intersects(kv.Value, query)).Select(kv => kv.Key), "random intersection");
            Same(tree.Contains(query), expected.Where(kv => Contains(query, kv.Value)).Select(kv => kv.Key), "random containment");
            var point = new Point(random.Next(-120, 121), random.Next(-120, 121), random.Next(-120, 121));
            CompareNearest(tree, expected, point, q == 0 ? double.PositiveInfinity : random.Next(0, 100), "random nearest");
        }
    }

    private static void CompareNearest(RTree<int> tree, Dictionary<int, Rectangle> expected, Point point, double limit, string message)
    {
        double distance = expected.Count == 0 ? double.PositiveInfinity : expected.Min(kv => Distance(kv.Value, point));
        var matches = expected.Where(kv => Distance(kv.Value, point) == distance && distance <= limit).Select(kv => kv.Key);
        Same(tree.Nearest(point, limit), matches, message);
    }

    private static void Same(IEnumerable<int> actual, IEnumerable<int> expected, string message)
    {
        var left = actual.OrderBy(i => i).ToArray(); var right = expected.OrderBy(i => i).ToArray(); queries++;
        Require(left.SequenceEqual(right), message + ": actual=[" + string.Join(",", left) + "] expected=[" + string.Join(",", right) + "]");
    }

    private static bool Equal(Rectangle a, Rectangle b)
    {
        return a != null && b != null && Enumerable.Range(0, 3).All(d => a.min[d] == b.min[d] && a.max[d] == b.max[d]);
    }

    private static bool Intersects(Rectangle a, Rectangle b)
    {
        for (int d = 0; d < 3; d++) if (a.min[d] > b.max[d] || a.max[d] < b.min[d]) return false;
        return true;
    }

    private static bool Contains(Rectangle outer, Rectangle inner)
    {
        for (int d = 0; d < 3; d++) if (outer.min[d] > inner.min[d] || outer.max[d] < inner.max[d]) return false;
        return true;
    }

    private static double Distance(Rectangle box, Point point)
    {
        double x = Math.Max(0, Math.Max(box.min[0] - point.coordinates[0], point.coordinates[0] - box.max[0]));
        double y = Math.Max(0, Math.Max(box.min[1] - point.coordinates[1], point.coordinates[1] - box.max[1]));
        double z = Math.Max(0, Math.Max(box.min[2] - point.coordinates[2], point.coordinates[2] - box.max[2]));
        double scale = Math.Max(x, Math.Max(y, z));
        if (scale == 0 || double.IsInfinity(scale)) return scale;
        return scale * Math.Sqrt((x / scale) * (x / scale) + (y / scale) * (y / scale) + (z / scale) * (z / scale));
    }

    private static bool Equal(Aabb left, Aabb right)
    {
        return left.MinX == right.MinX && left.MinY == right.MinY && left.MinZ == right.MinZ &&
               left.MaxX == right.MaxX && left.MaxY == right.MaxY && left.MaxZ == right.MaxZ;
    }

    private static bool Equal(Aabb box, Rectangle rectangle)
    {
        return rectangle != null && box.MinX == rectangle.min[0] && box.MinY == rectangle.min[1] && box.MinZ == rectangle.min[2] &&
               box.MaxX == rectangle.max[0] && box.MaxY == rectangle.max[1] && box.MaxZ == rectangle.max[2];
    }

    private static void Validate(RTree<int> tree, Dictionary<int, Rectangle> expected)
    {
        Require(tree.Count == expected.Count, "Count differs from live item count");
        var nodes = Field<TreeNode[]>(tree, "nodes");
        var entries = Field<TreeEntry[]>(tree, "entries");
        var values = Field<int[]>(tree, "itemValues");
        var liveItems = Field<bool[]>(tree, "itemLive");
        var ids = Field<Dictionary<int, int>>(tree, "itemIds");
        int nextNode = Field<int>(tree, "nextNodeId"), nextItem = Field<int>(tree, "nextItemId");
        Require(ids.Count == expected.Count, "Item lookup contains orphan mappings");
        Require(values.Length == liveItems.Length, "Item pool arrays have different capacities");
        Require(nextNode >= 1 && nextNode <= nodes.Length && nextItem >= 0 && nextItem <= values.Length, "Invalid pool high-water mark");
        foreach (int item in expected.Keys)
        {
            int slot;
            Require(ids.TryGetValue(item, out slot) && slot >= 0 && slot < values.Length && liveItems[slot] && values[slot] == item,
                "Broken item-to-slot mapping");
        }
        var visited = new HashSet<int>(); var leafIds = new HashSet<int>();
        int root = tree.getRootNodeId();
        Require(root >= 0 && root < nodes.Length && nodes[root].Live, "Root is absent from the live node pool");
        Require(nodes[root].Level == Field<int>(tree, "treeHeight"), "Root level differs from tree height");
        int count = ValidateNode(nodes, entries, root, true, tree.minNodeEntries, tree.maxNodeEntries, visited, leafIds, values, liveItems, expected);
        Require(count == expected.Count, "Leaf entry cardinality differs from Count");
        int liveNodeCount = 0, liveItemCount = 0;
        for (int i = 0; i < nodes.Length; i++) if (nodes[i].Live) { Require(i < nextNode, "Live node is beyond the allocation high-water mark"); liveNodeCount++; }
        for (int i = 0; i < liveItems.Length; i++)
        {
            if (liveItems[i]) { Require(i < nextItem, "Live item is beyond the allocation high-water mark"); liveItemCount++; }
            else Require(values[i] == default(int), "Inactive item slot retained a value");
        }
        Require(visited.Count == liveNodeCount, "Node pool retains unreachable live nodes");
        Require(liveItemCount == expected.Count, "Item pool contains unreachable live slots");
        var freeNodes = Field<Stack<int>>(tree, "freeNodeIds");
        var freeItems = Field<Stack<int>>(tree, "freeItemIds");
        Require(freeNodes.Count == freeNodes.Distinct().Count(), "Duplicate ID in free node list");
        Require(freeItems.Count == freeItems.Distinct().Count(), "Duplicate ID in free item list");
        foreach (int id in freeNodes) Require(id >= 0 && id < nextNode && !nodes[id].Live, "Free node ID is still live or invalid");
        foreach (int id in freeItems) Require(id >= 0 && id < nextItem && !liveItems[id], "Free item ID is still live or invalid");
        Require(liveNodeCount + freeNodes.Count == nextNode, "Allocated node IDs leaked outside the live/free partition");
        Require(liveItemCount + freeItems.Count == nextItem, "Allocated item IDs leaked outside the live/free partition");
        if (expected.Count == 0)
        {
            Require(tree.getBounds() == null, "Empty tree bounds are nonnull");
            Require(liveNodeCount == 1 && nodes[root].Level == 1, "Empty tree must contain one leaf root");
        }
        else
        {
            var low = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var high = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var box in expected.Values) for (int d = 0; d < 3; d++) { low[d] = Math.Min(low[d], box.min[d]); high[d] = Math.Max(high[d], box.max[d]); }
            Require(Equal(tree.getBounds(), new Rectangle(low, high)), "Tree bounds differ from the live rectangles' exact union");
        }
    }

    private static int ValidateNode(TreeNode[] nodes, TreeEntry[] entries, int nodeId, bool root, int minimum, int maximum,
        HashSet<int> visited, HashSet<int> leafIds, int[] values, bool[] liveItems, Dictionary<int, Rectangle> expected)
    {
        nodeChecks++;
        Require(visited.Add(nodeId), "Cycle or reused live node");
        TreeNode node = nodes[nodeId];
        Require(node.Live, "Traversal reached a released node");
        Require(node.Count >= 0 && node.Count <= maximum, "Node exceeds capacity");
        Require(root || node.Count >= minimum, "Nonroot node is below minimum occupancy");
        Require(!root || node.Level == 1 || node.Count >= 2, "Internal root was not collapsed");
        if (node.Count == 0) { Require(root && node.Level == 1, "An empty nonroot survived"); return 0; }
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        int leaves = 0;
        for (int i = 0; i < node.Count; i++)
        {
            int offset = checked(nodeId * maximum + i);
            Require(offset < entries.Length, "Node entry lies outside the flat entry pool");
            TreeEntry entry = entries[offset];
            minX = Math.Min(minX, entry.Bounds.MinX); minY = Math.Min(minY, entry.Bounds.MinY); minZ = Math.Min(minZ, entry.Bounds.MinZ);
            maxX = Math.Max(maxX, entry.Bounds.MaxX); maxY = Math.Max(maxY, entry.Bounds.MaxY); maxZ = Math.Max(maxZ, entry.Bounds.MaxZ);
            if (node.Level == 1)
            {
                Require(entry.Id >= 0 && entry.Id < values.Length && liveItems[entry.Id], "Leaf ID has no live item slot");
                Require(leafIds.Add(entry.Id), "Duplicate leaf ID");
                int item = values[entry.Id];
                Rectangle box;
                Require(expected.TryGetValue(item, out box) && Equal(entry.Bounds, box), "Leaf bounds/item differ from oracle");
                leaves++;
            }
            else
            {
                Require(entry.Id >= 0 && entry.Id < nodes.Length && nodes[entry.Id].Live, "Child node is absent from live pool");
                TreeNode child = nodes[entry.Id];
                Require(child.Level == node.Level - 1, "Child has incorrect level");
                Require(Equal(entry.Bounds, child.Bounds), "Parent entry differs from child MBR");
                leaves += ValidateNode(nodes, entries, entry.Id, false, minimum, maximum, visited, leafIds, values, liveItems, expected);
            }
        }
        Require(node.Bounds.MinX == minX && node.Bounds.MinY == minY && node.Bounds.MinZ == minZ &&
                node.Bounds.MaxX == maxX && node.Bounds.MaxY == maxY && node.Bounds.MaxZ == maxZ,
                "Node MBR is not its entries' exact union");
        return leaves;
    }
}
