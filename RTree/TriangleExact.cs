using System;
using System.Numerics;

namespace RTree
{
    // Adaptive fallback for triangle predicates. All finite binary64 inputs
    // become exact integers in a common dyadic unit. No geometric epsilon is
    // used: thin triangles and intersections on edges retain their identity.
    internal static class TriangleExact
    {
        private struct Integer3
        {
            internal BigInteger X, Y, Z;
            internal Integer3(Vector3d value, int shift)
            {
                X = Integer(value.X, shift); Y = Integer(value.Y, shift); Z = Integer(value.Z, shift);
            }
            internal Integer3(BigInteger x, BigInteger y, BigInteger z) { X = x; Y = y; Z = z; }
        }

        internal static bool HasArea(Vector3d a, Vector3d b, Vector3d c)
        {
            int shift = Math.Min(Shift(a), Math.Min(Shift(b), Shift(c)));
            if (shift == int.MaxValue) return false;
            Integer3 origin = new Integer3(a, shift);
            Integer3 first = Subtract(new Integer3(b, shift), origin);
            Integer3 second = Subtract(new Integer3(c, shift), origin);
            Integer3 normal = Cross(first, second);
            return !normal.X.IsZero || !normal.Y.IsZero || !normal.Z.IsZero;
        }

        internal static bool Intersects(Triangle3 triangle, Ray3 ray, out double parameter)
        {
            parameter = 0;
            int shift = Math.Min(Math.Min(Shift(triangle.A), Shift(triangle.B)),
                Math.Min(Shift(triangle.C), Math.Min(Shift(ray.Origin), Shift(ray.Direction))));
            Integer3 a = new Integer3(triangle.A, shift);
            Integer3 first = Subtract(new Integer3(triangle.B, shift), a);
            Integer3 second = Subtract(new Integer3(triangle.C, shift), a);
            Integer3 direction = new Integer3(ray.Direction, shift);
            Integer3 p = Cross(direction, second);
            BigInteger determinant = Dot(first, p);
            if (determinant.IsZero) return false;
            Integer3 origin = Subtract(new Integer3(ray.Origin, shift), a);
            BigInteger u = Dot(origin, p);
            Integer3 q = Cross(origin, first);
            BigInteger v = Dot(direction, q);
            BigInteger numerator = Dot(second, q);
            if (determinant.Sign < 0)
            {
                determinant = -determinant; u = -u; v = -v; numerator = -numerator;
            }
            if (u.Sign < 0 || v.Sign < 0 || u + v > determinant || numerator.Sign < 0) return false;
            if (CompareRatio(numerator, determinant, ray.TMin) < 0) return false;
            if (!double.IsPositiveInfinity(ray.TMax) && CompareRatio(numerator, determinant, ray.TMax) > 0) return false;
            double result = Ratio(numerator, determinant);
            if (double.IsInfinity(result)) return false;
            parameter = result;
            return true;
        }

        private static Integer3 Subtract(Integer3 a, Integer3 b)
        { return new Integer3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        private static Integer3 Cross(Integer3 a, Integer3 b)
        { return new Integer3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }

        private static BigInteger Dot(Integer3 a, Integer3 b)
        { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        // Shift is measured from 2^-1074. Removing powers common to every
        // input makes ordinary integer-coordinate fallbacks inexpensive.
        private static int Shift(Vector3d value)
        { return Math.Min(Shift(value.X), Math.Min(Shift(value.Y), Shift(value.Z))); }

        private static int Shift(double value)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value) & 0x7fffffffffffffffUL;
            if (bits == 0) return int.MaxValue;
            int exponent = (int)(bits >> 52);
            ulong mantissa = bits & 0x000fffffffffffffUL;
            if (exponent != 0) mantissa |= 0x0010000000000000UL;
            return (exponent == 0 ? 0 : exponent - 1) + TrailingZeros(mantissa);
        }

        private static BigInteger Integer(double value, int commonShift)
        {
            long signedBits = BitConverter.DoubleToInt64Bits(value);
            ulong bits = (ulong)signedBits & 0x7fffffffffffffffUL;
            if (bits == 0) return BigInteger.Zero;
            int exponent = (int)(bits >> 52);
            ulong mantissa = bits & 0x000fffffffffffffUL;
            if (exponent != 0) mantissa |= 0x0010000000000000UL;
            int trailing = TrailingZeros(mantissa);
            BigInteger result = new BigInteger((long)(mantissa >> trailing)) <<
                ((exponent == 0 ? 0 : exponent - 1) + trailing - commonShift);
            return signedBits < 0 ? -result : result;
        }

        private static int TrailingZeros(ulong value)
        {
            int result = 0;
            if ((value & 0xffffffffUL) == 0) { result += 32; value >>= 32; }
            if ((value & 0xffffUL) == 0) { result += 16; value >>= 16; }
            if ((value & 0xffUL) == 0) { result += 8; value >>= 8; }
            if ((value & 0xfUL) == 0) { result += 4; value >>= 4; }
            if ((value & 0x3UL) == 0) { result += 2; value >>= 2; }
            if ((value & 0x1UL) == 0) result++;
            return result;
        }

        private static int CompareRatio(BigInteger numerator, BigInteger denominator, double value)
        {
            if (value == 0) return numerator.Sign;
            long bits = BitConverter.DoubleToInt64Bits(value);
            int exponent = (int)((bits >> 52) & 2047);
            long mantissa = bits & 0x000fffffffffffffL;
            if (exponent != 0) mantissa |= 0x0010000000000000L;
            int power = exponent == 0 ? -1074 : exponent - 1075;
            BigInteger right = denominator * mantissa;
            return power >= 0 ? numerator.CompareTo(right << power) : (numerator << -power).CompareTo(right);
        }

        // Correctly rounded, nearest-even positive rational conversion. Only
        // the quotient's final 53 bits (or subnormal units) become a double.
        private static double Ratio(BigInteger numerator, BigInteger denominator)
        {
            if (numerator.IsZero) return 0;
            int exponent = BitLength(numerator) - BitLength(denominator);
            if (exponent >= 0)
            {
                if (numerator < (denominator << exponent)) exponent--;
            }
            else if ((numerator << -exponent) < denominator) exponent--;
            if (exponent > 1023) return double.PositiveInfinity;
            if (exponent < -1075) return 0;
            bool subnormal = exponent < -1022;
            int shift = subnormal ? 1074 : 52 - exponent;
            BigInteger dividend = shift >= 0 ? numerator << shift : numerator;
            BigInteger divisor = shift >= 0 ? denominator : denominator << -shift;
            BigInteger remainder;
            BigInteger quotient = BigInteger.DivRem(dividend, divisor, out remainder);
            int comparison = (remainder << 1).CompareTo(divisor);
            if (comparison > 0 || (comparison == 0 && !quotient.IsEven)) quotient++;
            if (subnormal) return BitConverter.Int64BitsToDouble((long)quotient);
            if (quotient == 0x0020000000000000L)
            {
                quotient >>= 1;
                if (++exponent > 1023) return double.PositiveInfinity;
            }
            long fraction = (long)quotient - 0x0010000000000000L;
            return BitConverter.Int64BitsToDouble(((long)(exponent + 1023) << 52) | fraction);
        }

        private static int BitLength(BigInteger value)
        {
            byte[] bytes = value.ToByteArray();
            int last = bytes.Length - 1;
            while (last > 0 && bytes[last] == 0) last--;
            int bits = last * 8;
            byte top = bytes[last];
            while (top != 0) { bits++; top >>= 1; }
            return bits;
        }
    }
}
