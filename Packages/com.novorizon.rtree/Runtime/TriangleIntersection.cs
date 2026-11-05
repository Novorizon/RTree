using System;

namespace RTree
{
    // The fast path certifies signs with floating-point error bounds. Difficult
    // boundary, nearly parallel, and extreme-range cases use exact dyadics.
    internal static class TriangleIntersection
    {
        private const double ErrorFactor = 1.4210854715202004e-14; // 64 * machine epsilon
        private const double MinimumNormal = 2.2250738585072014e-308;

        private struct Approximation
        {
            internal double Value, Error;
            internal Approximation(double value, double error) { Value = value; Error = error; }
        }

        internal static bool HasArea(Vector3d a, Vector3d b, Vector3d c)
        {
            if (a.Equals(b) || a.Equals(c) || b.Equals(c)) return false;
            int constantAxes = (a.X == b.X && a.X == c.X ? 1 : 0) +
                (a.Y == b.Y && a.Y == c.Y ? 1 : 0) + (a.Z == b.Z && a.Z == c.Z ? 1 : 0);
            if (constantAxes >= 2) return false;
            double x1 = b.X - a.X, y1 = b.Y - a.Y, z1 = b.Z - a.Z;
            double x2 = c.X - a.X, y2 = c.Y - a.Y, z2 = c.Z - a.Z;
            if (CertifiedCross(y1, z2, z1, y2) || CertifiedCross(z1, x2, x1, z2) || CertifiedCross(x1, y2, y1, x2))
                return true;
            return TriangleExact.HasArea(a, b, c);
        }

        private static bool CertifiedCross(double a, double b, double c, double d)
        {
            if (!Ordinary(a) || !Ordinary(b) || !Ordinary(c) || !Ordinary(d)) return false;
            double p = a * b, q = c * d;
            if (!Ordinary(p) || !Ordinary(q) || (p == 0 && a != 0 && b != 0) || (q == 0 && c != 0 && d != 0)) return false;
            return Math.Abs(p - q) > ErrorFactor * (Math.Abs(p) + Math.Abs(q));
        }

        internal static bool Intersects(Triangle3 triangle, Ray3 ray, out double parameter)
        {
            ray.Validate(); parameter = 0;
            if (!triangle.HasArea) return false;
            // An axis-constant plane and a parallel ray are exactly parallel;
            // this common rejection needs no arbitrary-precision fallback.
            if (((triangle.ConstantAxesMask & 1) != 0 && ray.Direction.X == 0) ||
                ((triangle.ConstantAxesMask & 2) != 0 && ray.Direction.Y == 0) ||
                ((triangle.ConstantAxesMask & 4) != 0 && ray.Direction.Z == 0)) return false;
            int kz = Math.Abs(ray.Direction.X) >= Math.Abs(ray.Direction.Y) ? 0 : 1;
            if (Math.Abs(ray.Direction.Z) > Math.Abs(ray.Direction.Axis(kz))) kz = 2;
            int kx = (kz + 1) % 3, ky = (kx + 1) % 3;
            if (ray.Direction.Axis(kz) < 0) { int swap = kx; kx = ky; ky = swap; }
            double largestDirection = ray.Direction.Axis(kz);
            double sx = ray.Direction.Axis(kx) / largestDirection;
            double sy = ray.Direction.Axis(ky) / largestDirection;
            if (!Ordinary(sx) || !Ordinary(sy) ||
                (sx == 0 && ray.Direction.Axis(kx) != 0) || (sy == 0 && ray.Direction.Axis(ky) != 0))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            double scale = MaximumCoordinate(triangle, ray.Origin);
            if (scale == 0) return false;
            Approximation az, bz, cz, ax, ay, bx, by, cx, cy;
            if (!Difference(triangle.A.Axis(kz), ray.Origin.Axis(kz), scale, out az) ||
                !Difference(triangle.B.Axis(kz), ray.Origin.Axis(kz), scale, out bz) ||
                !Difference(triangle.C.Axis(kz), ray.Origin.Axis(kz), scale, out cz) ||
                !Project(triangle.A.Axis(kx), ray.Origin.Axis(kx), scale, sx, az, out ax) ||
                !Project(triangle.A.Axis(ky), ray.Origin.Axis(ky), scale, sy, az, out ay) ||
                !Project(triangle.B.Axis(kx), ray.Origin.Axis(kx), scale, sx, bz, out bx) ||
                !Project(triangle.B.Axis(ky), ray.Origin.Axis(ky), scale, sy, bz, out by) ||
                !Project(triangle.C.Axis(kx), ray.Origin.Axis(kx), scale, sx, cz, out cx) ||
                !Project(triangle.C.Axis(ky), ray.Origin.Axis(ky), scale, sy, cz, out cy))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            double projected = Math.Max(Math.Max(Math.Abs(ax.Value), Math.Abs(ay.Value)),
                Math.Max(Math.Max(Math.Abs(bx.Value), Math.Abs(by.Value)), Math.Max(Math.Abs(cx.Value), Math.Abs(cy.Value))));
            if (projected == 0) return TriangleExact.Intersects(triangle, ray, out parameter);
            if (!Normalize(ref ax, projected) || !Normalize(ref ay, projected) || !Normalize(ref bx, projected) ||
                !Normalize(ref by, projected) || !Normalize(ref cx, projected) || !Normalize(ref cy, projected))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            Approximation u, v, w;
            if (!Cross(cx, by, cy, bx, out u) || !Cross(ax, cy, ay, cx, out v) || !Cross(bx, ay, by, ax, out w))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            if ((u.Value < 0 || v.Value < 0 || w.Value < 0) && (u.Value > 0 || v.Value > 0 || w.Value > 0)) return false;
            double determinant = u.Value + v.Value + w.Value;
            double determinantError = u.Error + v.Error + w.Error + ErrorFactor * Math.Abs(determinant);
            double p = u.Value * az.Value, q = v.Value * bz.Value, r = w.Value * cz.Value;
            if (!ProductOrdinary(p, u.Value, az.Value) || !ProductOrdinary(q, v.Value, bz.Value) || !ProductOrdinary(r, w.Value, cz.Value))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            double numerator = p + q + r;
            double numeratorError = Math.Abs(u.Value) * az.Error + Math.Abs(az.Value) * u.Error + u.Error * az.Error +
                Math.Abs(v.Value) * bz.Error + Math.Abs(bz.Value) * v.Error + v.Error * bz.Error +
                Math.Abs(w.Value) * cz.Error + Math.Abs(cz.Value) * w.Error + w.Error * cz.Error +
                ErrorFactor * (Math.Abs(p) + Math.Abs(q) + Math.Abs(r));
            if (!Ordinary(numerator) || Math.Abs(numerator) <= numeratorError)
                return TriangleExact.Intersects(triangle, ray, out parameter);
            double sign = largestDirection < 0 ? -1 : 1;
            double hit = SpatialMath.ProductRatio(numerator, scale, determinant * sign, Math.Abs(largestDirection));
            if (double.IsNaN(hit) || double.IsInfinity(hit)) return TriangleExact.Intersects(triangle, ray, out parameter);
            double relativeError = numeratorError / Math.Abs(numerator) + determinantError / Math.Abs(determinant) + ErrorFactor;
            double hitError = Math.Abs(hit) * relativeError;
            // Interval comparisons close to a rounded endpoint are exact. The
            // tolerance decides only whether to fall back, never whether to hit.
            if (!Ordinary(hit) || double.IsInfinity(hitError) || Math.Abs(hit - ray.TMin) <= hitError ||
                (!double.IsInfinity(ray.TMax) && Math.Abs(hit - ray.TMax) <= hitError))
                return TriangleExact.Intersects(triangle, ray, out parameter);
            if (hit < ray.TMin || hit > ray.TMax) return false;
            parameter = hit; return true;
        }

        private static double MaximumCoordinate(Triangle3 triangle, Vector3d origin)
        {
            double scale = Math.Max(Math.Abs(origin.X), Math.Max(Math.Abs(origin.Y), Math.Abs(origin.Z)));
            scale = Math.Max(scale, Math.Max(Math.Abs(triangle.A.X), Math.Max(Math.Abs(triangle.A.Y), Math.Abs(triangle.A.Z))));
            scale = Math.Max(scale, Math.Max(Math.Abs(triangle.B.X), Math.Max(Math.Abs(triangle.B.Y), Math.Abs(triangle.B.Z))));
            return Math.Max(scale, Math.Max(Math.Abs(triangle.C.X), Math.Max(Math.Abs(triangle.C.Y), Math.Abs(triangle.C.Z))));
        }

        private static bool Difference(double coordinate, double origin, double scale, out Approximation result)
        {
            double difference = coordinate - origin;
            double value, error;
            if (double.IsInfinity(difference))
            {
                double a = coordinate / scale, b = origin / scale;
                value = a - b; error = ErrorFactor * (Math.Abs(a) + Math.Abs(b) + Math.Abs(value));
            }
            else { value = difference / scale; error = ErrorFactor * Math.Abs(value); }
            result = new Approximation(value, error);
            return Ordinary(value) && (value != 0 || coordinate == origin);
        }

        private static bool Project(double coordinate, double origin, double scale, double shear, Approximation depth, out Approximation result)
        {
            Approximation axis;
            if (!Difference(coordinate, origin, scale, out axis)) { result = default(Approximation); return false; }
            double product = shear * depth.Value;
            double value = axis.Value - product;
            double error = axis.Error + Math.Abs(shear) * depth.Error +
                ErrorFactor * (Math.Abs(axis.Value) + Math.Abs(product) + Math.Abs(value));
            result = new Approximation(value, error);
            return Ordinary(value) && ProductOrdinary(product, shear, depth.Value);
        }

        private static bool Normalize(ref Approximation value, double scale)
        {
            value.Value /= scale;
            value.Error = value.Error / scale + ErrorFactor * Math.Abs(value.Value);
            return Ordinary(value.Value) && !double.IsInfinity(value.Error);
        }

        private static bool Cross(Approximation a, Approximation b, Approximation c, Approximation d, out Approximation result)
        {
            double p = a.Value * b.Value, q = c.Value * d.Value;
            double value = p - q;
            double error = Math.Abs(a.Value) * b.Error + Math.Abs(b.Value) * a.Error + a.Error * b.Error +
                Math.Abs(c.Value) * d.Error + Math.Abs(d.Value) * c.Error + c.Error * d.Error +
                ErrorFactor * (Math.Abs(p) + Math.Abs(q));
            result = new Approximation(value, error);
            return ProductOrdinary(p, a.Value, b.Value) && ProductOrdinary(q, c.Value, d.Value) &&
                Ordinary(value) && Math.Abs(value) > error;
        }

        private static bool ProductOrdinary(double product, double a, double b)
        { return Ordinary(product) && (product != 0 || a == 0 || b == 0); }
        private static bool Ordinary(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && (value == 0 || Math.Abs(value) >= MinimumNormal); }
    }
}
