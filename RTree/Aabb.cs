using System;
using System.Runtime.CompilerServices;

namespace RTree
{
    // A box is six inline doubles in the tree's entry pool. Public Rectangle
    // objects are converted only at the API boundary, not while traversing.
    internal struct Aabb
    {
        internal double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        internal Aabb(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        internal static Aabb FromRectangle(Rectangle rectangle)
        {
            if (rectangle == null) throw new ArgumentNullException("rectangle");
            return new Aabb(rectangle.min[0], rectangle.min[1], rectangle.min[2],
                            rectangle.max[0], rectangle.max[1], rectangle.max[2]);
        }

        internal Rectangle ToRectangle()
        {
            return new Rectangle(MinX, MinY, MaxX, MaxY, MinZ, MaxZ);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Intersects(Aabb other)
        {
            return MinX <= other.MaxX && MaxX >= other.MinX &&
                   MinY <= other.MaxY && MaxY >= other.MinY &&
                   MinZ <= other.MaxZ && MaxZ >= other.MinZ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(Aabb other)
        {
            return MinX <= other.MinX && MaxX >= other.MaxX &&
                   MinY <= other.MinY && MaxY >= other.MaxY &&
                   MinZ <= other.MinZ && MaxZ >= other.MaxZ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool EqualsBounds(Aabb other)
        {
            return MinX == other.MinX && MaxX == other.MaxX &&
                   MinY == other.MinY && MaxY == other.MaxY &&
                   MinZ == other.MinZ && MaxZ == other.MaxZ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Aabb Union(Aabb left, Aabb right)
        {
            return new Aabb(Math.Min(left.MinX, right.MinX), Math.Min(left.MinY, right.MinY), Math.Min(left.MinZ, right.MinZ),
                            Math.Max(left.MaxX, right.MaxX), Math.Max(left.MaxY, right.MaxY), Math.Max(left.MaxZ, right.MaxZ));
        }

        internal double Min(int axis)
        {
            return axis == 0 ? MinX : axis == 1 ? MinY : MinZ;
        }

        internal double Max(int axis)
        {
            return axis == 0 ? MaxX : axis == 1 ? MaxY : MaxZ;
        }

        internal double Center(int axis)
        {
            double minimum = Min(axis), maximum = Max(axis);
            // The equality shortcut also preserves single-point subnormals.
            return minimum == maximum ? minimum : minimum * 0.5 + maximum * 0.5;
        }

        internal double Volume()
        {
            return Product(MaxX - MinX, MaxY - MinY, MaxZ - MinZ);
        }

        internal double Growth(Aabb other)
        {
            double x = MaxX - MinX, y = MaxY - MinY, z = MaxZ - MinZ;
            double gx = AxisGrowth(MinX, MaxX, other.MinX, other.MaxX);
            double gy = AxisGrowth(MinY, MaxY, other.MinY, other.MaxY);
            double gz = AxisGrowth(MinZ, MaxZ, other.MinZ, other.MaxZ);
            // Nonnegative expansion terms avoid infinity - infinity when both
            // original and expanded volumes exceed the double range.
            return Product(gx, y + gy, z + gz) + Product(x, gy, z + gz) + Product(x, y, gz);
        }

        internal double Surface()
        {
            double x = MaxX - MinX, y = MaxY - MinY, z = MaxZ - MinZ;
            return Product(x, y) + Product(x, z) + Product(y, z);
        }

        internal double SurfaceGrowth(Aabb other)
        {
            double x = MaxX - MinX, y = MaxY - MinY, z = MaxZ - MinZ;
            double gx = AxisGrowth(MinX, MaxX, other.MinX, other.MaxX);
            double gy = AxisGrowth(MinY, MaxY, other.MinY, other.MaxY);
            double gz = AxisGrowth(MinZ, MaxZ, other.MinZ, other.MaxZ);
            return Product(gx, y + gy) + Product(x, gy) +
                   Product(gx, z + gz) + Product(x, gz) +
                   Product(gy, z + gz) + Product(y, gz);
        }

        internal double Margin()
        {
            return (MaxX - MinX) + (MaxY - MinY) + (MaxZ - MinZ);
        }

        internal double MarginGrowth(Aabb other)
        {
            return AxisGrowth(MinX, MaxX, other.MinX, other.MaxX) +
                   AxisGrowth(MinY, MaxY, other.MinY, other.MaxY) +
                   AxisGrowth(MinZ, MaxZ, other.MinZ, other.MaxZ);
        }

        internal double OverlapVolume(Aabb other)
        {
            double x, y, z;
            return OverlapWidths(other, out x, out y, out z) ? Product(x, y, z) : 0;
        }

        internal double OverlapSurface(Aabb other)
        {
            double x, y, z;
            return OverlapWidths(other, out x, out y, out z) ? Product(x, y) + Product(x, z) + Product(y, z) : 0;
        }

        internal double OverlapMargin(Aabb other)
        {
            double x, y, z;
            return OverlapWidths(other, out x, out y, out z) ? x + y + z : 0;
        }

        private bool OverlapWidths(Aabb other, out double x, out double y, out double z)
        {
            double minX = Math.Max(MinX, other.MinX), maxX = Math.Min(MaxX, other.MaxX);
            double minY = Math.Max(MinY, other.MinY), maxY = Math.Min(MaxY, other.MaxY);
            double minZ = Math.Max(MinZ, other.MinZ), maxZ = Math.Min(MaxZ, other.MaxZ);
            x = maxX - minX; y = maxY - minY; z = maxZ - minZ;
            return maxX >= minX && maxY >= minY && maxZ >= minZ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double AxisGrowth(double minimum, double maximum, double otherMinimum, double otherMaximum)
        {
            return Math.Max(0, minimum - otherMinimum) + Math.Max(0, otherMaximum - maximum);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Product(double x, double y)
        {
            return x == 0 || y == 0 ? 0 : x * y;
        }

        private static double Product(double x, double y, double z)
        {
            double product = (x * y) * z;
            if (product > 0 && !double.IsInfinity(product)) return product;
            if (x == 0 || y == 0 || z == 0) return 0;
            if (x > y) { double swap = x; x = y; y = swap; }
            if (y > z) { double swap = y; y = z; z = swap; }
            if (x > y) { double swap = x; x = y; y = swap; }
            return (x * z) * y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal DistanceKey Distance(double px, double py, double pz)
        {
            double x = Gap(MinX, MaxX, px), y = Gap(MinY, MaxY, py), z = Gap(MinZ, MaxZ, pz);
            double squared = x * x + y * y + z * z;
            if (squared >= DistanceKey.MinimumNormal && !double.IsInfinity(squared))
                return new DistanceKey(squared, 1);
            if (x == 0 && y == 0 && z == 0) return new DistanceKey(0, 0);
            if (double.IsInfinity(squared))
            {
                double distance = Norm(x, y, z);
                if (!double.IsInfinity(distance))
                {
                    // An overflowing squared sum can still round to a norm
                    // on the ordinary side of the category boundary.
                    if (distance <= DistanceKey.MaximumOrdinaryDistance)
                        return new DistanceKey(distance * distance, 1);
                    return new DistanceKey(distance * DistanceKey.ScaleDown, 2);
                }
                return new DistanceKey(ScaledDistance(px, py, pz, DistanceKey.ScaleDown), 2);
            }
            double smallDistance = Norm(x, y, z);
            if (smallDistance >= DistanceKey.MinimumOrdinaryDistance)
                return new DistanceKey(smallDistance * smallDistance, 1);
            if (smallDistance >= DistanceKey.MinimumNormal)
                return new DistanceKey(smallDistance * DistanceKey.ScaleUp, 0);
            // Scale before the final norm is rounded when the distance itself
            // is subnormal, retaining Epsilon axis/diagonal distinctions.
            return new DistanceKey(ScaledDistance(px, py, pz, DistanceKey.ScaleUp), 0);
        }

        private double ScaledDistance(double px, double py, double pz, double scale)
        {
            return Norm(ScaledGap(MinX, MaxX, px, scale),
                        ScaledGap(MinY, MaxY, py, scale),
                        ScaledGap(MinZ, MaxZ, pz, scale));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Gap(double minimum, double maximum, double point)
        {
            if (point < minimum) return minimum - point;
            if (point > maximum) return point - maximum;
            return 0;
        }

        private static double ScaledGap(double minimum, double maximum, double point, double scale)
        {
            // Test containment before scaling so a zero-gap axis cannot turn
            // into infinity - infinity when tiny distances are scaled up.
            if (point < minimum) return minimum * scale - point * scale;
            if (point > maximum) return point * scale - maximum * scale;
            return 0;
        }

        private static double Norm(double x, double y, double z)
        {
            double squared = x * x + y * y + z * z;
            if (squared >= DistanceKey.MinimumNormal && !double.IsInfinity(squared)) return Math.Sqrt(squared);
            double scale = Math.Max(x, Math.Max(y, z));
            if (scale == 0 || double.IsInfinity(scale)) return scale;
            x /= scale; y /= scale; z /= scale;
            return scale * Math.Sqrt(x * x + y * y + z * z);
        }
    }

    // Ordinary comparisons stay squared; extreme norms and nearby rounding
    // ties use a norm fallback. An unbounded limit ranks last.
    internal struct DistanceKey
    {
        internal const double MinimumNormal = 2.2250738585072014e-308;
        internal const double ScaleUp = 1.3407807929942597e154; // 2^512
        internal const double ScaleDown = 7.4583407312002067e-155; // 2^-512
        internal const double MinimumOrdinaryDistance = 1.4916681462400413e-154; // sqrt(MinimumNormal)
        internal const double MaximumOrdinaryDistance = 1.3407807929942596e154; // sqrt(double.MaxValue)
        internal readonly double value;
        internal readonly int rank;

        internal DistanceKey(double value, int rank)
        {
            this.value = value; this.rank = rank;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int CompareTo(DistanceKey other)
        {
            if (rank != other.rank) return rank.CompareTo(other.rank);
            if (value == other.value) return 0;
            if (rank == 1)
            {
                // Distinct nearby squares can have the same rounded norm.
                // Preserve the public nearest-tie semantics without taking a
                // square root for well-separated or exactly equal candidates.
                long difference = BitConverter.DoubleToInt64Bits(value) - BitConverter.DoubleToInt64Bits(other.value);
                if (difference >= -4 && difference <= 4)
                    return Math.Sqrt(value).CompareTo(Math.Sqrt(other.value));
            }
            return value.CompareTo(other.value);
        }

        internal static DistanceKey Limit(double radius)
        {
            if (double.IsNaN(radius) || radius < 0) throw new ArgumentOutOfRangeException("radius");
            if (double.IsPositiveInfinity(radius)) return new DistanceKey(0, 3);
            double squared = radius * radius;
            if (double.IsInfinity(squared)) return new DistanceKey(radius * ScaleDown, 2);
            if (squared < MinimumNormal) return new DistanceKey(radius * ScaleUp, 0);

            // Squaring a rounded radius can round inward: sqrt(3)^2 is the
            // predecessor of 3. Choose the last squared value whose rounded
            // square root still satisfies the inclusive limit. This work is
            // done once per query; ordinary candidate distances stay squared.
            while (Math.Sqrt(squared) > radius)
                squared = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(squared) - 1);
            while (squared < double.MaxValue)
            {
                double next = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(squared) + 1);
                if (Math.Sqrt(next) > radius) break;
                squared = next;
            }
            return new DistanceKey(squared, 1);
        }
    }
}
