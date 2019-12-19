﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Spell.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Spell
{
    public class Telegraph
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public UnitEntity Caster { get; }
        public Vector3 Position { get; }
        public Vector3 Rotation { get; }
        public TelegraphDamageEntry TelegraphDamage { get; }

        public Telegraph(TelegraphDamageEntry telegraphDamageEntry, UnitEntity caster, Vector3 position, Vector3 rotation)
        {
            TelegraphDamage = telegraphDamageEntry;
            Caster          = caster;
            Position        = position;
            Rotation        = rotation;
        }

        /// <summary>
        /// Returns any <see cref="UnitEntity"/> inside the <see cref="Telegraph"/>.
        /// </summary>
        public IEnumerable<UnitEntity> GetTargets()
        {
            Caster.Map.Search(Position, GridSearchSize(), new SearchCheckTelegraph(this, Caster), out List<GridEntity> targets);
            return targets.Select(t => t as UnitEntity);
        }

        /// <summary>
        /// Returns whether the supplied <see cref="Vector3"/> is inside the telegraph.
        /// </summary>
        public bool InsideTelegraph(Vector3 position)
        {
            switch ((DamageShape)TelegraphDamage.DamageShapeEnum)
            {
                case DamageShape.Circle:
                    return Vector3.Distance(Position, position) < TelegraphDamage.Param00;
                case DamageShape.Cone:
                {
                    float angleRadian = Position.GetAngle(position);
                    angleRadian -= Rotation.X;
                    angleRadian = angleRadian.NormaliseRadians();

                    float angleDegrees = MathF.Abs(angleRadian.ToDegrees());
                    if (angleDegrees > TelegraphDamage.Param02 / 2f)
                        return false;

                    return Vector3.Distance(Position, position) < TelegraphDamage.Param01;
                }
                case DamageShape.LongCone:
                {
                    float angleRadian = Position.GetAngle(position);
                    angleRadian -= Rotation.X;
                    angleRadian = angleRadian.NormaliseRadians();

                    float angleDegrees = MathF.Abs(angleRadian.ToDegrees());
                    if (angleDegrees > TelegraphDamage.Param02 / 2f)
                        return false;

                    return Vector3.Distance(Position, position) < TelegraphDamage.Param01;
                }
                case DamageShape.Quadrilateral:
                {
                    float telegraphLength = TelegraphDamage.Param01;
                    float telegraphHeight = TelegraphDamage.Param02;

                    float leftAngle = -Rotation.X + MathF.PI / 2;
                    Vector3 bottomLeft = Position.GetPoint2D(leftAngle, telegraphLength / 2f);
                    Vector3 bottomRight = Position.GetPoint2D(leftAngle + MathF.PI / 2, telegraphLength / 2f);
                    Vector3 topRight = bottomRight.GetPoint2D(-Rotation.X - MathF.PI / 4, telegraphLength);
                    Vector3 topLeft = bottomLeft.GetPoint2D(-Rotation.X - MathF.PI / 4, telegraphLength);
                    List<Vector3> points = new List<Vector3>
                    {
                        bottomLeft,
                        bottomRight,
                        topRight,
                        topLeft
                    };

                    return PointInPolygon(points.ToArray(), position.X, position.Z) && position.Y <= Position.Y + telegraphHeight && position.Y >= Position.Y - telegraphHeight;
                }
                case DamageShape.Rectangle:
                {
                    float Width = TelegraphDamage.Param00;
                    float Length = TelegraphDamage.Param01;
                    float Height = TelegraphDamage.Param02;

                    //Find the point offsets in location to the player
                    var bottomRight = new Vector3(-Width, 0, 0);
                    var bottomLeft = new Vector3(Width, 0, 0);
                    var topRight = new Vector3(-Width, 0, Length);
                    var topLeft = new Vector3(Width, 0, Length);

                    //Translate the points back to the global cordinate system and rotate them the same
                    //way the player if facing
                    bottomLeft = Vector3.Add(RotatePoint(bottomLeft, Rotation.X), Position);
                    bottomRight = Vector3.Add(RotatePoint(bottomRight, Rotation.X), Position);
                    topLeft = Vector3.Add(RotatePoint(topLeft, Rotation.X), Position);
                    topRight = Vector3.Add(RotatePoint(topRight, Rotation.X), Position);


                    //Create a polygon to test with the rotated points
                    Vector2[] RotatedRectangle = new Vector2[]{
                        new Vector2(bottomLeft.X, bottomLeft.Z),
                        new Vector2(bottomRight.X, bottomRight.Z),
                        new Vector2(topLeft.X, topLeft.Z),
                        new Vector2(topRight.X, topRight.Z)
                    };

                    return IsPointInPolygon(RotatedRectangle, new Vector2(position.X, position.Z)) && position.Y <= Position.Y + Height && Position.Y - Height <= position.Y;
                }
                default:
                    log.Warn($"Unhandled telegraph shape {(DamageShape)TelegraphDamage.DamageShapeEnum}.");
                    return false;
            }
        }

        private float GridSearchSize()
        {
            switch ((DamageShape)TelegraphDamage.DamageShapeEnum)
            {
                case DamageShape.Circle:
                    return TelegraphDamage.Param00;
                case DamageShape.Cone:
                case DamageShape.LongCone:
                    return TelegraphDamage.Param01;
                case DamageShape.Quadrilateral:
                case DamageShape.Rectangle:
                    return TelegraphDamage.Param01;
                default:
                    return 0f;
            }
        }

        private bool IsPointInPolygon(Vector2[] polygon, Vector2 testPoint)
        {
            bool result = false;
            int j = polygon.Length - 1;
            for (int i = 0; i < polygon.Length; i++)
            {
                if (polygon[i].Y < testPoint.Y && polygon[j].Y >= testPoint.Y || polygon[j].Y < testPoint.Y && polygon[i].Y >= testPoint.Y)
                {
                    if (polygon[i].X + (testPoint.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) * (polygon[j].X - polygon[i].X) < testPoint.X)
                    {
                        result = !result;
                    }
                }
                j = i;
            }
            return result;
        }

        private Vector3 RotatePoint(Vector3 Point, float Rotation)
        {
            var newX = ((Point.X) * MathF.Cos(Rotation) + (Point.Z) * MathF.Sin(Rotation));
            var newY = ((-Point.X) * MathF.Sin(Rotation) + (Point.Z) * MathF.Cos(Rotation));

            newX = -newX;
            newY = -newY;

            return new Vector3(newX, Point.Y, newY);
        }
        /// <summary>
        /// Returns a boolean whether a point sits in a horizontal plane of points
        /// </summary>
        private bool PointInPolygon(Vector3[] Points, float X, float Z)
        {
            // Get the angle between the point and the
            // first and last vertices.
            int maxPoint = Points.Length - 1;
            float total_angle = GetAngle(
                Points[maxPoint].X, Points[maxPoint].Z,
                X, Z,
                Points[0].X, Points[0].Z);

            // Add the angles from the point to each other pair of vertices.
            for (int i = 0; i < maxPoint; i++)
            {
                total_angle += GetAngle(
                    Points[i].X, Points[i].Z,
                    X, Z,
                    Points[i + 1].X, Points[i + 1].Z);
            }

            // The total angle should be 2 * PI or -2 * PI if the point is in the polygon and close to zero if the point is outside the polygon.
            return (Math.Abs(total_angle) > 0.000001);
        }

        #region "Cross and Dot Products"
        /// <summary>
        /// Get the total angle between 3 vertices
        /// </summary>
        private float GetAngle(float Ax, float Ay, float Bx, float By, float Cx, float Cy)
        {
            // Get the dot product.
            float dot_product = DotProduct(Ax, Ay, Bx, By, Cx, Cy);

            // Get the cross product.
            float cross_product = CrossProductLength(Ax, Ay, Bx, By, Cx, Cy);

            // Calculate the angle.
            return (float)Math.Atan2(cross_product, dot_product);
        }

        // Return the cross product AB x BC.
        // The cross product is a vector perpendicular to AB
        // and BC having length |AB| * |BC| * Sin(theta) and
        // with direction given by the right-hand rule.
        // For two vectors in the X-Y plane, the result is a
        // vector with X and Y components 0 so the Z component
        // gives the vector's length and direction.
        private float CrossProductLength(float Ax, float Ay,
            float Bx, float By, float Cx, float Cy)
        {
            // Get the vectors' coordinates.
            float BAx = Ax - Bx;
            float BAy = Ay - By;
            float BCx = Cx - Bx;
            float BCy = Cy - By;

            // Calculate the Z coordinate of the cross product.
            return (BAx * BCy - BAy * BCx);
        }

        // Return the dot product AB · BC.
        // Note that AB · BC = |AB| * |BC| * Cos(theta).
        private float DotProduct(float Ax, float Ay,
            float Bx, float By, float Cx, float Cy)
        {
            // Get the vectors' coordinates.
            float BAx = Ax - Bx;
            float BAy = Ay - By;
            float BCx = Cx - Bx;
            float BCy = Cy - By;

            // Calculate the dot product.
            return (BAx * BCx + BAy * BCy);
        }
        #endregion // Cross and Dot Products
    }
}
