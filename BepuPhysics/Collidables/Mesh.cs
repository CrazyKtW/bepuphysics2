﻿using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BepuPhysics.Collidables
{
    public struct Mesh : IMeshShape
    {
        public Tree Tree;
        public Buffer<Triangle> Triangles;
        internal Vector3 scale;
        internal Vector3 inverseScale;
        public Vector3 Scale
        {
            get
            {
                return scale;
            }
            set
            {
                Debug.Assert(value.X != 0 && value.Y != 0 && value.Z != 0, "All components of scale must be nonzero.");
                scale = value;
                inverseScale = Vector3.One / value;
            }
        }
        /// <summary>
        /// Precalculated maximum radius of any triangle in the shape measured from the shape origin.
        /// </summary>
        /// <remarks>This should be recomputed if the triangle data is changed.</remarks>
        public float MaximumRadius;
        /// <summary>
        /// Precalculated maximum angular expansion allowed for bounding boxes.
        /// </summary>
        /// <remarks>This should be recomputed if the compound children are changed.</remarks>
        public float MaximumAngularExpansion;

        public Mesh(Buffer<Triangle> triangles, Vector3 scale, BufferPool pool) : this()
        {
            Triangles = triangles;
            Tree = new Tree(pool, triangles.Length);
            pool.Take<BoundingBox>(triangles.Length, out var boundingBoxes);
            for (int i = 0; i < triangles.Length; ++i)
            {
                ref var t = ref triangles[i];
                ref var bounds = ref boundingBoxes[i];
                bounds.Min = Vector3.Min(t.A, Vector3.Min(t.B, t.C));
                bounds.Max = Vector3.Max(t.A, Vector3.Max(t.B, t.C));
            }
            Tree.SweepBuild(pool, boundingBoxes.Slice(0, triangles.Length));
            Scale = scale;
            ComputeAngularExpansionData(ref Triangles, out MaximumRadius, out MaximumAngularExpansion);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void GetLocalTriangle(int triangleIndex, out Triangle target)
        {
            ref var source = ref Triangles[triangleIndex];
            target.A = scale * source.A;
            target.B = scale * source.B;
            target.C = scale * source.C;
        }

        public void ComputeBounds(in BepuUtilities.Quaternion orientation, out Vector3 min, out Vector3 max)
        {
            Matrix3x3.CreateFromQuaternion(orientation, out var r);
            min = new Vector3(float.MaxValue);
            max = new Vector3(-float.MaxValue);
            for (int i = 0; i < Triangles.Length; ++i)
            {
                //This isn't an ideal bounding box calculation for a mesh. 
                //-You might be able to get a win out of widely vectorizing.
                //-Indexed smooth meshes would tend to have a third as many max/min operations.
                //-Even better would be a set of extreme points that are known to fully enclose the mesh, eliminating the need to test the vast majority.
                //But optimizing this only makes sense if dynamic meshes are common, and they really, really, really should not be.
                ref var triangle = ref Triangles[i];
                Matrix3x3.Transform(scale * triangle.A, r, out var a);
                Matrix3x3.Transform(scale * triangle.B, r, out var b);
                Matrix3x3.Transform(scale * triangle.C, r, out var c);
                var min0 = Vector3.Min(a, b);
                var min1 = Vector3.Min(c, min);
                var max0 = Vector3.Max(a, b);
                var max1 = Vector3.Max(c, max);
                min = Vector3.Min(min0, min1);
                max = Vector3.Max(max0, max1);
            }
        }

        static void ComputeAngularExpansionData(ref Buffer<Triangle> triangles, out float maximumRadius, out float maximumAngularExpansion)
        {
            var minSquared = float.MaxValue;
            var maxSquared = float.MinValue;
            for (int i = 0; i < triangles.Length; ++i)
            {
                ref var triangle = ref triangles[i];
                var a = triangle.A.LengthSquared();
                var b = triangle.B.LengthSquared();
                var c = triangle.C.LengthSquared();
                if (minSquared > a)
                    minSquared = a;
                if (minSquared > b)
                    minSquared = b;
                if (minSquared > c)
                    minSquared = c;

                if (maxSquared < a)
                    maxSquared = a;
                if (maxSquared < b)
                    maxSquared = b;
                if (maxSquared < c)
                    maxSquared = c;
            }

            maximumRadius = (float)Math.Sqrt(maxSquared);
            //The minimum radius value is actually a lower bound, but that works fine as a maximumAngularExpansion.
            maximumAngularExpansion = maximumRadius - (float)Math.Sqrt(minSquared);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ComputeAngularExpansionData(out float maximumRadius, out float maximumAngularExpansion)
        {
            //This is too expensive to compute on the fly, so just use precomputed values.
            maximumRadius = MaximumRadius;
            maximumAngularExpansion = MaximumAngularExpansion;
        }

        public ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        {
            return new MeshShapeBatch<Mesh>(pool, initialCapacity);
        }

        unsafe struct LeafTester : IRayLeafTester
        {
            Triangle* triangles;
            public float MinimumT;
            public Vector3 MinimumNormal;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LeafTester(in Buffer<Triangle> triangles)
            {
                this.triangles = (Triangle*)triangles.Memory;
                MinimumT = float.MaxValue;
                MinimumNormal = default;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe void TestLeaf(int leafIndex, RayData* rayData, float* maximumT)
            {
                ref var triangle = ref triangles[leafIndex];
                if (Triangle.RayTest(triangle.A, triangle.B, triangle.C, rayData->Origin, rayData->Direction, out var t, out var normal) && t < MinimumT && t <= *maximumT)
                {
                    MinimumT = t;
                    MinimumNormal = normal;
                }
            }
        }

        public bool RayTest(in RigidPose pose, in Vector3 origin, in Vector3 direction, float maximumT, out float t, out Vector3 normal)
        {
            BepuUtilities.Quaternion.Conjugate(pose.Orientation, out var conjugate);
            Matrix3x3.CreateFromQuaternion(conjugate, out var inverseOrientation);
            Matrix3x3.Transform(origin - pose.Position, inverseOrientation, out var localOrigin);
            Matrix3x3.Transform(direction, inverseOrientation, out var localDirection);
            localOrigin *= inverseScale;
            localDirection *= inverseScale;
            var leafTester = new LeafTester(Triangles);
            Tree.RayCast(localOrigin, localDirection, maximumT, ref leafTester);
            if (leafTester.MinimumT < float.MaxValue)
            {
                t = leafTester.MinimumT;
                normal = leafTester.MinimumNormal;
                return true;
            }
            t = default;
            normal = default;
            return false;
        }

        public unsafe void RayTest<TRayHitHandler>(in RigidPose pose, ref RaySource rays, ref TRayHitHandler hitHandler) where TRayHitHandler : struct, IShapeRayHitHandler
        {
            //TODO: Note that we dispatch a bunch of scalar tests here. You could be more clever than this- batched tests are possible. 
            //May be worth creating a different traversal designed for low ray counts- might be able to get some benefit out of a semidynamic packet or something.
            BepuUtilities.Quaternion.Conjugate(pose.Orientation, out var conjugate);
            Matrix3x3.CreateFromQuaternion(conjugate, out var inverseOrientation);
            var leafTester = new LeafTester(Triangles);
            for (int i = 0; i < rays.RayCount; ++i)
            {
                rays.GetRay(i, out var ray, out var maximumT);
                Matrix3x3.Transform(ray->Origin - pose.Position, inverseOrientation, out var localOrigin);
                Matrix3x3.Transform(ray->Direction, inverseOrientation, out var localDirection);
                localOrigin *= inverseScale;
                localDirection *= inverseScale;
                leafTester.MinimumT = float.MaxValue;
                Tree.RayCast(localOrigin, localDirection, *maximumT, ref leafTester);
                if (leafTester.MinimumT < float.MaxValue)
                {
                    hitHandler.OnRayHit(i, leafTester.MinimumT, leafTester.MinimumNormal);
                }
            }
        }


        unsafe struct Enumerator<TSubpairOverlaps> : IBreakableForEach<int> where TSubpairOverlaps : ICollisionTaskSubpairOverlaps
        {
            public BufferPool Pool;
            public void* Overlaps;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool LoopBody(int i)
            {
                Unsafe.AsRef<TSubpairOverlaps>(Overlaps).Allocate(Pool) = i;
                return true;
            }
        }

        unsafe struct SweepLeafTester<TOverlaps> : ISweepLeafTester where TOverlaps : ICollisionTaskSubpairOverlaps
        {
            public BufferPool Pool;
            public void* Overlaps;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void TestLeaf(int leafIndex, ref float maximumT)
            {
                Unsafe.AsRef<TOverlaps>(Overlaps).Allocate(Pool) = leafIndex;
            }
        }

        public unsafe void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(PairsToTestForOverlap* pairs, int count, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
            where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
            where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        {
            //For now, we don't use anything tricky. Just traverse every child against the tree sequentially.
            //TODO: This sequentializes a whole lot of cache misses. You could probably get some benefit out of traversing all pairs 'simultaneously'- that is, 
            //using the fact that we have lots of independent queries to ensure the CPU always has something to do.
            Enumerator<TSubpairOverlaps> enumerator;
            enumerator.Pool = pool;
            for (int i = 0; i < count; ++i)
            {
                ref var pair = ref pairs[i];
                ref var mesh = ref Unsafe.AsRef<Mesh>(pair.Container);
                var scaledMin = mesh.inverseScale * pair.Min;
                var scaledMax = mesh.inverseScale * pair.Max;
                enumerator.Overlaps = Unsafe.AsPointer(ref overlaps.GetOverlapsForPair(i));
                Tree.GetOverlaps(scaledMin, scaledMax, ref enumerator);
            }
        }

        public unsafe void FindLocalOverlaps<TOverlaps>(in Vector3 min, in Vector3 max, in Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
            where TOverlaps : ICollisionTaskSubpairOverlaps
        {
            var scaledMin = min * inverseScale;
            var scaledMax = max * inverseScale;
            var scaledSweep = sweep * inverseScale;
            SweepLeafTester<TOverlaps> enumerator;
            enumerator.Pool = pool;
            enumerator.Overlaps = overlaps;
            Tree.Sweep(scaledMin, scaledMax, scaledSweep, maximumT, ref enumerator);
        }


        public void Dispose(BufferPool bufferPool)
        {
            bufferPool.Return(ref Triangles);
            Tree.Dispose(bufferPool);
        }


        /// <summary>
        /// Type id of mesh shapes.
        /// </summary>
        public const int Id = 6;
        public int TypeId => Id;

    }
}
