//   Rectangle.java
//   Java Spatial Index Library
//   Copyright (C) 2002 Infomatiq Limited
//  
//  This library is free software; you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation; either
//  version 2.1 of the License, or (at your option) any later version.
//  
//  This library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//  Lesser General Public License for more details.
//  
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307 USA

//  Ported to C# By Dror Gluska, April 9th, 2009
//  Updated by Boris Kudryashov, January 29, 2014
using System;

namespace RTree
{

    /**
     * An axis-aligned box in three dimensions.
     * 
     * @author  aled@sourceforge.net
     * @version 1.0b2p1
     */
    public class Rectangle
    {
        /**
         * Number of dimensions in a rectangle. In theory this
         * could be exended to three or more dimensions.
         */
        internal const int DIMENSIONS = 3;

        /**
         * Maximum value for each dimension: { max(x), max(y), max(z) }.
         */
        internal double[] max;

        /**
         * Minimum value for each dimension: { min(x), min(y), min(z) }.
         */
        internal double[] min;

        /**
         * Constructor.
         * 
         * @param x1 coordinate of any corner of the rectangle
         * @param y1 (see x1)
         * @param x2 coordinate of the opposite corner
         * @param y2 (see x2)
         */
        public Rectangle(double x1, double y1, double x2, double y2, double z1, double z2)
        {
            min = new double[DIMENSIONS];
            max = new double[DIMENSIONS];
            set(x1, y1, x2, y2, z1, z2);
        }

        /**
         * Constructor.
         * 
         * @param min array containing the minimum value for each dimension; ie { min(x), min(y) }
         * @param max array containing the maximum value for each dimension; ie { max(x), max(y) }
         */
        public Rectangle(double[] min, double[] max)
        {
            if (min == null)
                throw new ArgumentNullException("min");
            if (max == null)
                throw new ArgumentNullException("max");
            if (min.Length != DIMENSIONS)
                throw new ArgumentException("Bounds must have three coordinates.", "min");
            if (max.Length != DIMENSIONS)
                throw new ArgumentException("Bounds must have three coordinates.", "max");
            for (int i = 0; i < DIMENSIONS; i++)
            {
                ValidateCoordinate(min[i], "min");
                ValidateCoordinate(max[i], "max");
                if (min[i] > max[i])
                    throw new ArgumentException("Each minimum must be less than or equal to its maximum.", "min");
            }

            this.min = new double[DIMENSIONS];
            this.max = new double[DIMENSIONS];

            set(min, max);
        }

        /**
          * Sets the size of the rectangle.
          * 
          * @param x1 coordinate of any corner of the rectangle
          * @param y1 (see x1)
          * @param x2 coordinate of the opposite corner
          * @param y2 (see x2)
          */
        internal void set(double x1, double y1, double x2, double y2, double z1, double z2)
        {
            ValidateCoordinate(x1, "x1");
            ValidateCoordinate(y1, "y1");
            ValidateCoordinate(x2, "x2");
            ValidateCoordinate(y2, "y2");
            ValidateCoordinate(z1, "z1");
            ValidateCoordinate(z2, "z2");
            min[0] = Math.Min(x1, x2);
            min[1] = Math.Min(y1, y2);
            min[2] = Math.Min(z1, z2);
            max[0] = Math.Max(x1, x2);
            max[1] = Math.Max(y1, y2);
            max[2] = Math.Max(z1, z2);
        }

        internal static void ValidateCoordinate(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("Coordinates must be finite.", parameterName);
        }

        /**
         * Sets the size of the rectangle.
         * 
         * @param min array containing the minimum value for each dimension; ie { min(x), min(y) }
         * @param max array containing the maximum value for each dimension; ie { max(x), max(y) }
         */
        internal void set(double[] min, double[] max)
        {
            System.Array.Copy(min, 0, this.min, 0, DIMENSIONS);
            System.Array.Copy(max, 0, this.max, 0, DIMENSIONS);
        }

        /**
         * Make a copy of this rectangle
         * 
         * @return copy of this rectangle
         */
        internal Rectangle copy()
        {
            return new Rectangle(min, max);
        }

        /**
         * Determine whether an edge of this rectangle overlies the equivalent 
         * edge of the passed rectangle
         */
        internal bool edgeOverlaps(Rectangle r)
        {
            for (int i = 0; i < DIMENSIONS; i++)
            {
                if (min[i] == r.min[i] || max[i] == r.max[i])
                {
                    return true;
                }
            }
            return false;
        }

        /**
         * Determine whether this rectangle intersects the passed rectangle
         * 
         * @param r The rectangle that might intersect this rectangle
         * 
         * @return true if the rectangles intersect, false if they do not intersect
         */
        internal bool intersects(Rectangle r)
        {
            // Every dimension must intersect. If any dimension
            // does not intersect, return false immediately.
            for (int i = 0; i < DIMENSIONS; i++)
            {
                if (max[i] < r.min[i] || min[i] > r.max[i])
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Determine whether this rectangle contains the passed rectangle
         * 
         * @param r The rectangle that might be contained by this rectangle
         * 
         * @return true if this rectangle contains the passed rectangle, false if
         *         it does not
         */
        internal bool contains(Rectangle r)
        {
            for (int i = 0; i < DIMENSIONS; i++)
            {
                if (max[i] < r.max[i] || min[i] > r.min[i])
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Determine whether this rectangle is contained by the passed rectangle
         * 
         * @param r The rectangle that might contain this rectangle
         * 
         * @return true if the passed rectangle contains this rectangle, false if
         *         it does not
         */
        internal bool containedBy(Rectangle r)
        {
            for (int i = 0; i < DIMENSIONS; i++)
            {
                if (max[i] > r.max[i] || min[i] < r.min[i])
                {
                    return false;
                }
            }
            return true;
        }

        /**
         * Return the distance between this rectangle and the passed point.
         * If the rectangle contains the point, the distance is zero.
         * 
         * @param p Point to find the distance to
         * 
         * @return distance beween this rectangle and the passed point.
         */
        internal double distance(Point p)
        {
            return Norm(PointGap(min[0], max[0], p.coordinates[0]),
                        PointGap(min[1], max[1], p.coordinates[1]),
                        PointGap(min[2], max[2], p.coordinates[2]));
        }

        // Used to compare distances larger than double.MaxValue. Scaling the
        // coordinates before subtracting also prevents gap overflow.
        internal double distanceScaled(Point p, double scale)
        {
            return Norm(PointGap(min[0] * scale, max[0] * scale, p.coordinates[0] * scale),
                        PointGap(min[1] * scale, max[1] * scale, p.coordinates[1] * scale),
                        PointGap(min[2] * scale, max[2] * scale, p.coordinates[2] * scale));
        }

        /**
         * Return the distance between this rectangle and the passed rectangle.
         * If the rectangles overlap, the distance is zero.
         * 
         * @param r Rectangle to find the distance to
         * 
         * @return distance between this rectangle and the passed rectangle
         */

        internal double distance(Rectangle r)
        {
            return Norm(IntervalGap(min[0], max[0], r.min[0], r.max[0]),
                        IntervalGap(min[1], max[1], r.min[1], r.max[1]),
                        IntervalGap(min[2], max[2], r.min[2], r.max[2]));
        }

        private static double PointGap(double minimum, double maximum, double point)
        {
            if (point < minimum)
                return minimum - point;
            if (point > maximum)
                return point - maximum;
            return 0;
        }

        private static double IntervalGap(double min1, double max1, double min2, double max2)
        {
            if (max1 < min2)
                return min2 - max1;
            if (max2 < min1)
                return min1 - max2;
            return 0;
        }

        private static double Norm(double x, double y, double z)
        {
            double squared = x * x + y * y + z * z;
            if (!double.IsInfinity(squared) && squared >= 2.2250738585072014e-308)
                return Math.Sqrt(squared);

            // Scaling preserves distances whose squares overflow or underflow.
            double scale = Math.Max(x, Math.Max(y, z));
            if (scale == 0 || double.IsInfinity(scale))
                return scale;
            x /= scale;
            y /= scale;
            z /= scale;
            return scale * Math.Sqrt(x * x + y * y + z * z);
        }

        /**
         * Return the squared distance from this rectangle to the passed point
         */
        internal double distanceSquared(int dimension, double point)
        {
            double gap = PointGap(min[dimension], max[dimension], point);
            return gap * gap;
        }

        /**
         * Return the furthst possible distance between this rectangle and
         * the passed rectangle. 
         * 
         * Find the distance between this rectangle and each corner of the
         * passed rectangle, and use the maximum.
         *
         */
        internal double furthestDistance(Rectangle r)
        {
            return Norm(Math.Max(PointGap(min[0], max[0], r.min[0]), PointGap(min[0], max[0], r.max[0])),
                        Math.Max(PointGap(min[1], max[1], r.min[1]), PointGap(min[1], max[1], r.max[1])),
                        Math.Max(PointGap(min[2], max[2], r.min[2]), PointGap(min[2], max[2], r.max[2])));
        }

        /**
         * Calculate the volume by which this box would be enlarged if
         * added to the passed rectangle. Neither rectangle is altered.
         * 
         * @param r Rectangle to union with this rectangle, in order to 
         *          compute the difference in area of the union and the
         *          original rectangle
         */
        internal double enlargement(Rectangle r)
        {
            double x = max[0] - min[0];
            double y = max[1] - min[1];
            double z = max[2] - min[2];
            double growX = Math.Max(0, min[0] - r.min[0]) + Math.Max(0, r.max[0] - max[0]);
            double growY = Math.Max(0, min[1] - r.min[1]) + Math.Max(0, r.max[1] - max[1]);
            double growZ = Math.Max(0, min[2] - r.min[2]) + Math.Max(0, r.max[2] - max[2]);

            // Expand one axis at a time instead of subtracting two volumes.
            // This avoids infinity - infinity and 0 * infinity for large or
            // degenerate boxes, keeping every insertion cost nonnegative.
            return Product(growX, y + growY, z + growZ) +
                   Product(x, growY, z + growZ) +
                   Product(x, y, growZ);
        }

        /**
         * Compute the volume of this three-dimensional box.
         * 
         * @return The area of this rectangle
         */
        internal double area()
        {
            return Product(max[0] - min[0], max[1] - min[1], max[2] - min[2]);
        }

        // Secondary insertion costs distinguish planar and linear boxes whose
        // volumes are all zero. The factor of two in surface area is omitted.
        internal double surfaceArea()
        {
            double x = max[0] - min[0];
            double y = max[1] - min[1];
            double z = max[2] - min[2];
            return Product(x, y) + Product(x, z) + Product(y, z);
        }

        internal double surfaceEnlargement(Rectangle r)
        {
            double x = max[0] - min[0];
            double y = max[1] - min[1];
            double z = max[2] - min[2];
            double growX = Math.Max(0, min[0] - r.min[0]) + Math.Max(0, r.max[0] - max[0]);
            double growY = Math.Max(0, min[1] - r.min[1]) + Math.Max(0, r.max[1] - max[1]);
            double growZ = Math.Max(0, min[2] - r.min[2]) + Math.Max(0, r.max[2] - max[2]);
            return Product(growX, y + growY) + Product(x, growY) +
                   Product(growX, z + growZ) + Product(x, growZ) +
                   Product(growY, z + growZ) + Product(y, growZ);
        }

        internal double margin()
        {
            return (max[0] - min[0]) + (max[1] - min[1]) + (max[2] - min[2]);
        }

        internal double marginEnlargement(Rectangle r)
        {
            return Math.Max(0, min[0] - r.min[0]) + Math.Max(0, r.max[0] - max[0]) +
                   Math.Max(0, min[1] - r.min[1]) + Math.Max(0, r.max[1] - max[1]) +
                   Math.Max(0, min[2] - r.min[2]) + Math.Max(0, r.max[2] - max[2]);
        }

        private static double Product(double x, double y)
        {
            return x == 0 || y == 0 ? 0 : x * y;
        }

        private static double Product(double x, double y, double z)
        {
            double product = (x * y) * z;
            if (product > 0 && !double.IsInfinity(product))
                return product;
            if (x == 0 || y == 0 || z == 0)
                return 0;
            // Multiply the smallest and largest finite factors first so an
            // intermediate product cannot overflow when the result fits.
            if (x > y) { double temp = x; x = y; y = temp; }
            if (y > z) { double temp = y; y = z; z = temp; }
            if (x > y) { double temp = x; x = y; y = temp; }
            return (x * z) * y;
        }

        /**
         * Computes the union of this rectangle and the passed rectangle, storing
         * the result in this rectangle.
         * 
         * @param r Rectangle to add to this rectangle
         */
        internal void add(Rectangle r)
        {
            for (int i = 0; i < DIMENSIONS; i++)
            {
                if (r.min[i] < min[i])
                {
                    min[i] = r.min[i];
                }
                if (r.max[i] > max[i])
                {
                    max[i] = r.max[i];
                }
            }
        }

        /**
         * Find the the union of this rectangle and the passed rectangle.
         * Neither rectangle is altered
         * 
         * @param r The rectangle to union with this rectangle
         */
        internal Rectangle union(Rectangle r)
        {
            Rectangle union = this.copy();
            union.add(r);
            return union;
        }

        internal bool CompareArrays(double[] a1, double[] a2)
        {
            if ((a1 == null) || (a2 == null))
                return false;
            if (a1.Length != a2.Length)
                return false;

            for (int i = 0; i < a1.Length; i++)
                if (a1[i] != a2[i])
                    return false;
            return true;
        }

        /**
         * Determine whether this rectangle is equal to a given object.
         * Equality is determined by the bounds of the rectangle.
         * 
         * @param o The object to compare with this rectangle
         */
        public override bool Equals(object obj)
        {
            Rectangle r = obj as Rectangle;
            return r != null && r.GetType() == GetType() &&
                   CompareArrays(r.min, min) && CompareArrays(r.max, max);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < DIMENSIONS; i++)
                {
                    hash = hash * 31 + (min[i] == 0 ? 0d : min[i]).GetHashCode();
                    hash = hash * 31 + (max[i] == 0 ? 0d : max[i]).GetHashCode();
                }
                return hash;
            }
        }

        /** 
         * Determine whether this rectangle is the same as another object
         * 
         * Note that two rectangles can be equal but not the same object, 
         * if they both have the same bounds.
         * 
         * @param o The object to compare with this rectangle.
         */
        internal bool sameObject(object o)
        {
            return base.Equals(o);
        }
    }
}
