using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
            Regression("finite extreme coordinates", ExtremeCoordinates);
            Regression("minimum subnormal nearest ordering", MinimumSubnormalNearest);
            Regression("item ID wrap and occupied IDs", ItemIdWrap);
            Regression("complete drain and node reuse", DrainAndReuse);
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
        Require(Field<Dictionary<int, string>>(references, "IdsToItems").Count == 0, "null item leaked a forward mapping");
        Require(Field<Dictionary<string, int>>(references, "ItemsToIds").Count == 0, "null item leaked a reverse mapping");
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
        Same(tree.Nearest(point, 10), new[] { 0, 1 }, "all ties, not all items within radius");
        Same(tree.Nearest(point, 1), new[] { 0, 1 }, "inclusive radius");
        Same(tree.Nearest(point, .99), new int[0], "nothing within distance limit");
        Same(tree.Nearest(point, double.PositiveInfinity), new[] { 0, 1 }, "unbounded nearest");
        tree.Add(Cube(0, 0), 3);
        Same(tree.Nearest(point, 0), new[] { 3 }, "zero distance limit");
        Same(tree.Nearest(new Point(100, 0, 0), 100), new[] { 2 }, "repeated query cannot retain prior ties");
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

    private static void ItemIdWrap()
    {
        var tree = new RTree<int>(); var expected = new Dictionary<int, Rectangle>();
        var counter = typeof(RTree<int>).GetField("idcounter", Fields);
        counter.SetValue(tree, int.MaxValue - 1);
        Add(tree, expected, 0, Box(0));
        Add(tree, expected, 1, Box(1));
        var ids = Field<Dictionary<int, int>>(tree, "ItemsToIds");
        Require(ids[0] == int.MaxValue && ids[1] == int.MinValue, "Item ID counter did not wrap");
        counter.SetValue(tree, int.MaxValue - 1);
        Add(tree, expected, 2, Box(2));
        Require(ids[2] == int.MinValue + 1, "Wrapped allocation did not skip occupied IDs");
        Delete(tree, expected, 1);
        counter.SetValue(tree, int.MaxValue);
        Add(tree, expected, 3, Box(3));
        Require(ids[3] == int.MinValue, "Wrapped allocation did not reuse a free ID");
        Same(tree.Intersects(Cube(-1, 100)), expected.Keys, "item queries remain correct after counter wrap");
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
        Require(Field<Dictionary<int, Node<int>>>(tree, "nodeMap")[tree.getRootNodeId()].level > 1, "Minimum-subnormal pruning fixture did not create internal nodes");
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
            Require(Field<Dictionary<int, Node<int>>>(tree, "nodeMap").Count == 1, "Empty tree retained unreachable nodes");
        }
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

    private static void Validate(RTree<int> tree, Dictionary<int, Rectangle> expected)
    {
        Require(tree.Count == expected.Count, "Count differs from live item count");
        var nodes = Field<Dictionary<int, Node<int>>>(tree, "nodeMap");
        var forward = Field<Dictionary<int, int>>(tree, "IdsToItems");
        var reverse = Field<Dictionary<int, int>>(tree, "ItemsToIds");
        Require(forward.Count == expected.Count && reverse.Count == expected.Count, "Item dictionaries contain orphan mappings");
        foreach (int item in expected.Keys) Require(reverse.ContainsKey(item) && forward[reverse[item]] == item, "Broken bidirectional mapping");
        var visited = new HashSet<int>(); var leafIds = new HashSet<int>();
        int count = ValidateNode(nodes, nodes[tree.getRootNodeId()], true, Field<int>(tree, "minNodeEntries"), tree.maxNodeEntries, visited, leafIds, forward, expected);
        Require(count == expected.Count, "Leaf entry cardinality differs from Count");
        Require(visited.Count == nodes.Count, "nodeMap retains unreachable nodes");
        var freeIds = Field<Stack<int>>(tree, "deletedNodeIds");
        Require(freeIds.Count == freeIds.Distinct().Count(), "Duplicate ID in free list");
        foreach (int id in freeIds) Require(!nodes.ContainsKey(id), "Free node ID is still live in nodeMap");
        if (expected.Count == 0)
        {
            Require(tree.getBounds() == null, "Empty tree bounds are nonnull");
            Require(nodes.Count == 1 && nodes[tree.getRootNodeId()].level == 1, "Empty tree must contain one leaf root");
        }
        else
        {
            var low = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var high = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var box in expected.Values) for (int d = 0; d < 3; d++) { low[d] = Math.Min(low[d], box.min[d]); high[d] = Math.Max(high[d], box.max[d]); }
            Require(Equal(tree.getBounds(), new Rectangle(low, high)), "Tree bounds differ from the live rectangles' exact union");
        }
    }

    private static int ValidateNode(Dictionary<int, Node<int>> nodes, Node<int> node, bool root, int minimum, int maximum,
        HashSet<int> visited, HashSet<int> leafIds, Dictionary<int, int> forward, Dictionary<int, Rectangle> expected)
    {
        nodeChecks++;
        Require(visited.Add(node.nodeId), "Cycle or reused live node");
        Require(node.entryCount >= 0 && node.entryCount <= maximum, "Node exceeds capacity");
        Require(root || node.entryCount >= minimum, "Nonroot node is below minimum occupancy");
        Require(!root || node.level == 1 || node.entryCount >= 2, "Internal root was not collapsed");
        if (node.entryCount == 0) { Require(root && node.mbr == null, "Empty node retains an MBR"); return 0; }
        var low = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var high = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        int leaves = 0;
        for (int i = 0; i < node.entryCount; i++)
        {
            Require(node.entries[i] != null, "Null entry inside active range");
            for (int d = 0; d < 3; d++) { low[d] = Math.Min(low[d], node.entries[i].min[d]); high[d] = Math.Max(high[d], node.entries[i].max[d]); }
            if (node.level == 1)
            {
                Require(leafIds.Add(node.ids[i]), "Duplicate leaf ID");
                Require(forward.ContainsKey(node.ids[i]), "Leaf ID has no item mapping");
                int item = forward[node.ids[i]];
                Require(expected.ContainsKey(item) && Equal(expected[item], node.entries[i]), "Leaf bounds/item differ from oracle");
                leaves++;
            }
            else
            {
                Require(nodes.ContainsKey(node.ids[i]), "Child node is absent from nodeMap");
                var child = nodes[node.ids[i]];
                Require(child.level == node.level - 1, "Child has incorrect level");
                Require(Equal(node.entries[i], child.mbr), "Parent entry differs from child MBR");
                leaves += ValidateNode(nodes, child, false, minimum, maximum, visited, leafIds, forward, expected);
            }
        }
        for (int i = node.entryCount; i < node.entries.Length; i++) Require(node.entries[i] == null, "Inactive array slot retains a rectangle");
        Require(Equal(node.mbr, new Rectangle(low, high)), "Node MBR is not its entries' exact union");
        return leaves;
    }
}
