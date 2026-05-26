using UnityEngine;

static class MeshPointPlacementUtility
{
    const float GeometryEpsilon = 0.000001f;

    internal struct MeshHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
    }

    // Returns true when the target hierarchy contains any mesh we can place points onto.
    public static bool HasPlaceableMesh(Component target)
    {
        if (target == null)
        {
            return false;
        }

        if (target.GetComponentsInChildren<SkinnedMeshRenderer>().Length > 0)
        {
            return true;
        }

        MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter != null && meshFilter.sharedMesh != null && meshFilter.sharedMesh.isReadable)
            {
                return true;
            }
        }

        return false;
    }

    // Counts static meshes that exist on the hierarchy but cannot be sampled because Read/Write is disabled.
    public static int CountUnreadableStaticMeshes(Component target)
    {
        if (target == null)
        {
            return 0;
        }

        int count = 0;
        MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter != null && meshFilter.sharedMesh != null && !meshFilter.sharedMesh.isReadable)
            {
                count++;
            }
        }

        return count;
    }

    // Finds the nearest triangle hit on any mesh under the target hierarchy.
    public static bool TryGetNearestMeshHit(Component target, Mesh bakedSkinnedMesh, Ray ray, out MeshHit closestHit)
    {
        closestHit = default;
        if (target == null)
        {
            return false;
        }

        bool foundHit = false;
        float closestDistance = float.PositiveInfinity;

        MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                continue;
            }

            if (!TryIntersectMesh(ray, meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, out MeshHit hit)
                || hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        SkinnedMeshRenderer[] skinnedMeshes = target.GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null || skinnedMesh.sharedMesh == null || bakedSkinnedMesh == null)
            {
                continue;
            }

            skinnedMesh.BakeMesh(bakedSkinnedMesh);
            if (!TryIntersectMesh(ray, bakedSkinnedMesh, skinnedMesh.transform.localToWorldMatrix, out MeshHit hit)
                || hit.distance >= closestDistance)
            {
                continue;
            }

            closestDistance = hit.distance;
            closestHit = hit;
            foundHit = true;
        }

        return foundHit;
    }

    // Intersects a world ray against every triangle in a mesh without relying on newer editor APIs.
    static bool TryIntersectMesh(Ray worldRay, Mesh mesh, Matrix4x4 localToWorld, out MeshHit closestHit)
    {
        closestHit = default;
        if (mesh == null || !mesh.isReadable)
        {
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length < 3)
        {
            return false;
        }

        Matrix4x4 worldToLocal = localToWorld.inverse;
        Vector3 localRayOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 localRayDirection = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localRayOrigin, localRayDirection);

        if (!mesh.bounds.IntersectRay(localRay))
        {
            return false;
        }

        bool foundHit = false;
        float closestWorldDistance = float.PositiveInfinity;

        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
        {
            Vector3 a = vertices[triangles[triangleIndex]];
            Vector3 b = vertices[triangles[triangleIndex + 1]];
            Vector3 c = vertices[triangles[triangleIndex + 2]];

            if (!TryIntersectTriangle(localRay, a, b, c, out float localDistance))
            {
                continue;
            }

            Vector3 localPoint = localRay.origin + (localRay.direction * localDistance);
            Vector3 worldPoint = localToWorld.MultiplyPoint3x4(localPoint);
            float worldDistance = Vector3.Distance(worldRay.origin, worldPoint);
            if (worldDistance >= closestWorldDistance)
            {
                continue;
            }

            Vector3 localNormal = Vector3.Cross(b - a, c - a);
            if (localNormal.sqrMagnitude <= GeometryEpsilon)
            {
                continue;
            }

            Vector3 worldNormal = worldToLocal.transpose.MultiplyVector(localNormal).normalized;
            if (Vector3.Dot(worldNormal, worldRay.direction) > 0f)
            {
                worldNormal = -worldNormal;
            }

            closestWorldDistance = worldDistance;
            closestHit = new MeshHit
            {
                point = worldPoint,
                normal = worldNormal,
                distance = worldDistance
            };
            foundHit = true;
        }

        return foundHit;
    }

    // Uses a Moller-Trumbore ray/triangle test in local mesh space.
    static bool TryIntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;

        Vector3 edgeAB = b - a;
        Vector3 edgeAC = c - a;
        Vector3 perpendicular = Vector3.Cross(ray.direction, edgeAC);
        float determinant = Vector3.Dot(edgeAB, perpendicular);
        if (Mathf.Abs(determinant) < GeometryEpsilon)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 triangleToRay = ray.origin - a;
        float u = Vector3.Dot(triangleToRay, perpendicular) * inverseDeterminant;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(triangleToRay, edgeAB);
        float v = Vector3.Dot(ray.direction, q) * inverseDeterminant;
        if (v < 0f || (u + v) > 1f)
        {
            return false;
        }

        float hitDistance = Vector3.Dot(edgeAC, q) * inverseDeterminant;
        if (hitDistance < 0f)
        {
            return false;
        }

        distance = hitDistance;
        return true;
    }
}
