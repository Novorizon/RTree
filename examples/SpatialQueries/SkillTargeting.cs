using System;
using System.Collections.Generic;
using RTree;

namespace SpatialQueriesDemo
{
    public sealed class Unit
    {
        public readonly int Id, Team;
        public Vector3d Position;
        public bool Alive = true;

        public Unit(int id, int team, Vector3d position) { Id = id; Team = team; Position = position; }
        public Bounds3 Bounds
        {
            get { return new Bounds3(Position.X-.5, Position.Y-.5, Position.Z-.5, Position.X+.5, Position.Y+.5, Position.Z+.5); }
        }
    }

    // Application policy belongs outside the spatial index. This demo uses
    // ordinary game coordinates, Y up, and center-based XZ ground targeting.
    // One service/result buffer per active query; candidates are reused.
    public sealed class SkillTargeting
    {
        private readonly IBoundsQuery3<Unit> index;
        private readonly List<Unit> candidates = new List<Unit>(64);

        public SkillTargeting(IBoundsQuery3<Unit> index)
        { if (index == null) throw new ArgumentNullException("index"); this.index = index; }

        public int GroundCircle(Vector3d center, double radius, double minY, double maxY, int casterTeam,
            List<Unit> results, QueryContext context)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0) throw new ArgumentOutOfRangeException("radius");
            var query = new Bounds3(center.X-radius,minY,center.Z-radius,center.X+radius,maxY,center.Z+radius);
            index.Query(query,candidates,context);
            results.Clear();
            double radiusSquared = radius*radius;
            foreach (Unit unit in candidates)
            {
                if (!unit.Alive || unit.Team == casterTeam || unit.Position.Y < minY || unit.Position.Y > maxY) continue;
                double x = unit.Position.X-center.X, z = unit.Position.Z-center.Z;
                if (x*x+z*z <= radiusSquared) results.Add(unit);
            }
            return results.Count;
        }

        public int GroundSector(Vector3d center, double radius, double minY, double maxY, Vector3d forward,
            double halfAngleDegrees, int casterTeam, List<Unit> results, QueryContext context)
        {
            double length = Math.Sqrt(forward.X*forward.X+forward.Z*forward.Z);
            if (length == 0 || double.IsInfinity(length)) throw new ArgumentException("Use a nonzero horizontal forward direction.","forward");
            if (double.IsNaN(halfAngleDegrees) || halfAngleDegrees < 0 || halfAngleDegrees > 180)
                throw new ArgumentOutOfRangeException("halfAngleDegrees");
            GroundCircle(center,radius,minY,maxY,casterTeam,results,context);
            double cosine = Math.Cos(halfAngleDegrees*Math.PI/180), fx = forward.X/length, fz = forward.Z/length;
            int count = 0;
            for (int i=0;i<results.Count;i++)
            {
                Unit unit = results[i]; double x=unit.Position.X-center.X, z=unit.Position.Z-center.Z;
                // A target at the origin is included; a real angle requires a
                // nonzero distance. Avoid a squared-dot test for sectors >180°.
                if (x*fx+z*fz >= cosine*Math.Sqrt(x*x+z*z)) results[count++] = unit;
            }
            if (count<results.Count) results.RemoveRange(count,results.Count-count);
            return count;
        }
    }
}
