using System;
using UnityEngine;
using UnityEngine.Serialization;

[ExecuteAlways]
[AddComponentMenu("Environment/Wind Field")]
public class WindField : MonoBehaviour
{
    const float MinFadeDistance = 0.001f;
    const float MinFeatureSize = 0.001f;
    const int GizmoCircleSegments = 32;
    const int WindParticleTextureSize = 64;
    const float MinParticleLifetime = 0.1f;
    const float MinParticleSize = 0.01f;
    const float MinParticleResponsiveness = 0.01f;
    const float MinParticleCount = 1f;
    const float ParticleBoundsPaddingFactor = 0.35f;

    [Serializable]
    public struct VortexFeature
    {
        // Localized wind features are authored in world XZ space because they belong to the environment.
        public Vector2 centerXZ;

        [Min(MinFeatureSize)]
        public float radius;

        public float tangentialSpeed;

        public VortexFeature(Vector2 centerXZ, float radius, float tangentialSpeed)
        {
            this.centerXZ = centerXZ;
            this.radius = Mathf.Max(MinFeatureSize, radius);
            this.tangentialSpeed = tangentialSpeed;
        }
    }

    [Serializable]
    public struct CurrentFeature
    {
        public Vector2 centerXZ;
        public Vector2 direction;

        [Min(MinFeatureSize)]
        public float width;

        public float speed;

        public CurrentFeature(Vector2 centerXZ, Vector2 direction, float width, float speed)
        {
            this.centerXZ = centerXZ;
            this.direction = direction;
            this.width = Mathf.Max(MinFeatureSize, width);
            this.speed = speed;
        }
    }

    public static WindField ActiveField { get; private set; }

    [Header("Wind")]
    // This is stored as a world-space velocity vector so direction and speed are edited together.
    public Vector3 baseWindVelocity = new Vector3(5f, 0f, 0f);

    [Header("Localized Wind")]
    public VortexFeature[] vortices = Array.Empty<VortexFeature>();
    public CurrentFeature[] currents = Array.Empty<CurrentFeature>();

    [Header("Water Filtering")]
    public bool fadeFromWaterline = true;

    [Min(0f)]
    public float fullStrengthHeight = 1f;

    [Header("Visualization")]
    [FormerlySerializedAs("showWindIndicators")]
    public bool showWindParticles;

    [FormerlySerializedAs("indicatorAreaCenterOffset")]
    public Vector3 particleAreaCenterOffset = new Vector3(0f, 1.25f, 0f);

    [FormerlySerializedAs("indicatorAreaSize")]
    public Vector3 particleAreaSize = new Vector3(12f, 2f, 12f);

    [FormerlySerializedAs("indicatorHeightAboveWater")]
    [Min(0f)]
    public float particleMinHeightAboveWater = 0.4f;

    [Min(0f)]
    public float particleMaxHeightAboveWater = 1.8f;

    [Range((int)MinParticleCount, 512)]
    public int particleCount = 96;

    [Min(MinParticleLifetime)]
    public float particleLifetime = 2.4f;

    [Min(MinParticleSize)]
    public float particleSize = 0.05f;

    [Min(0f)]
    public float particleSpeedScale = 0.35f;

    [Min(MinParticleResponsiveness)]
    public float particleResponsiveness = 4f;

    [Min(0f)]
    public float particleJitter = 0.08f;

    public float particleVerticalDrift = 0.02f;

    public Color particleColor = new Color(1f, 1f, 1f, 0.14f);

    [SerializeField, HideInInspector]
    ParticleSystem windParticleSystem;

    Material windParticleMaterial;
    Texture2D windParticleTexture;
    ParticleSystem.Particle[] windParticleBuffer = Array.Empty<ParticleSystem.Particle>();

    void OnEnable()
    {
        SanitizeSettings();
        DestroyLegacyWindIndicatorObject();
        ActiveField = this;
    }

    void OnValidate()
    {
        SanitizeSettings();
        DestroyLegacyWindIndicatorObject();
    }

    void OnDisable()
    {
        if (ActiveField == this)
        {
            ActiveField = null;
        }

        StopWindParticles();
        ReleaseWindParticleAssets();
    }

    void LateUpdate()
    {
        UpdateWindParticles();
    }

    // The query API already includes time and position so changing wind patterns can plug in later.
    public virtual Vector3 GetWindVelocityAtWorldPosition(Vector3 worldPosition, float timeSeconds)
    {
        return baseWindVelocity + GetLocalizedWindVelocity(worldPosition);
    }

    // Returns how much of the wind field should affect a point after waterline filtering.
    public virtual float GetWindStrengthMultiplierAtWorldPosition(Vector3 worldPosition, float timeSeconds)
    {
        if (!fadeFromWaterline)
        {
            return 1f;
        }

        if (!TryResolveWaterSurface(out UrpLowPolyWater water)
            || !water.TryGetSurfaceDataAtWorldPosition(worldPosition, timeSeconds, out float waterHeight, out _))
        {
            return 1f;
        }

        float safeFadeDistance = Mathf.Max(fullStrengthHeight, MinFadeDistance);
        return Mathf.Clamp01((worldPosition.y - waterHeight) / safeFadeDistance);
    }

    void SanitizeSettings()
    {
        fullStrengthHeight = Mathf.Max(0f, fullStrengthHeight);
        particleAreaSize.x = Mathf.Max(MinFeatureSize, particleAreaSize.x);
        particleAreaSize.y = Mathf.Max(MinFeatureSize, particleAreaSize.y);
        particleAreaSize.z = Mathf.Max(MinFeatureSize, particleAreaSize.z);
        particleMinHeightAboveWater = Mathf.Max(0f, particleMinHeightAboveWater);
        particleMaxHeightAboveWater = Mathf.Max(particleMinHeightAboveWater, particleMaxHeightAboveWater);
        particleCount = Mathf.Max((int)MinParticleCount, particleCount);
        particleLifetime = Mathf.Max(MinParticleLifetime, particleLifetime);
        particleSize = Mathf.Max(MinParticleSize, particleSize);
        particleSpeedScale = Mathf.Max(0f, particleSpeedScale);
        particleResponsiveness = Mathf.Max(MinParticleResponsiveness, particleResponsiveness);
        particleJitter = Mathf.Max(0f, particleJitter);
        SanitizeVortexFeatures();
        SanitizeCurrentFeatures();
    }

    void SanitizeVortexFeatures()
    {
        if (vortices == null)
        {
            vortices = Array.Empty<VortexFeature>();
            return;
        }

        for (int i = 0; i < vortices.Length; i++)
        {
            VortexFeature vortex = vortices[i];
            vortex.radius = Mathf.Max(MinFeatureSize, vortex.radius);
            vortices[i] = vortex;
        }
    }

    void SanitizeCurrentFeatures()
    {
        if (currents == null)
        {
            currents = Array.Empty<CurrentFeature>();
            return;
        }

        for (int i = 0; i < currents.Length; i++)
        {
            CurrentFeature current = currents[i];
            current.width = Mathf.Max(MinFeatureSize, current.width);
            if (current.direction.sqrMagnitude < 0.000001f)
            {
                current.direction = Vector2.right;
            }

            currents[i] = current;
        }
    }

    Vector3 GetLocalizedWindVelocity(Vector3 worldPosition)
    {
        Vector3 localizedVelocity = Vector3.zero;
        Vector2 worldXZ = new Vector2(worldPosition.x, worldPosition.z);
        VortexFeature[] activeVortices = vortices ?? Array.Empty<VortexFeature>();
        CurrentFeature[] activeCurrents = currents ?? Array.Empty<CurrentFeature>();

        for (int i = 0; i < activeVortices.Length; i++)
        {
            localizedVelocity += GetVortexVelocity(activeVortices[i], worldXZ);
        }

        for (int i = 0; i < activeCurrents.Length; i++)
        {
            localizedVelocity += GetCurrentVelocity(activeCurrents[i], worldXZ);
        }

        return localizedVelocity;
    }

    Vector3 GetVortexVelocity(VortexFeature vortex, Vector2 worldXZ)
    {
        if (Mathf.Abs(vortex.tangentialSpeed) <= 0f)
        {
            return Vector3.zero;
        }

        Vector2 offset = worldXZ - vortex.centerXZ;
        float distance = offset.magnitude;
        float safeRadius = Mathf.Max(vortex.radius, MinFeatureSize);
        if (distance <= 0.0001f || distance >= safeRadius)
        {
            return Vector3.zero;
        }

        Vector2 tangent = new Vector2(-offset.y, offset.x).normalized;
        float falloff = GetSmoothFalloff(distance, safeRadius);
        Vector2 planarVelocity = tangent * (vortex.tangentialSpeed * falloff);
        return new Vector3(planarVelocity.x, 0f, planarVelocity.y);
    }

    Vector3 GetCurrentVelocity(CurrentFeature current, Vector2 worldXZ)
    {
        Vector2 direction = NormalizeDirection(current.direction);
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        Vector2 offset = worldXZ - current.centerXZ;
        float perpendicularDistance = Mathf.Abs(Vector2.Dot(offset, perpendicular));
        float safeHalfWidth = Mathf.Max(current.width * 0.5f, MinFeatureSize);
        if (perpendicularDistance >= safeHalfWidth)
        {
            return Vector3.zero;
        }

        float falloff = GetSmoothFalloff(perpendicularDistance, safeHalfWidth);
        Vector2 planarVelocity = direction * (current.speed * falloff);
        return new Vector3(planarVelocity.x, 0f, planarVelocity.y);
    }

    float GetSmoothFalloff(float distance, float range)
    {
        float normalizedDistance = Mathf.Clamp01(distance / Mathf.Max(range, MinFeatureSize));
        return Mathf.SmoothStep(0f, 1f, 1f - normalizedDistance);
    }

    Vector2 NormalizeDirection(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.000001f)
        {
            return Vector2.right;
        }

        return direction.normalized;
    }

    bool TryResolveWaterSurface(out UrpLowPolyWater water)
    {
        water = UrpLowPolyWater.ActiveSurface;
        if (water != null)
        {
            return true;
        }

        water = FindAnyObjectByType<UrpLowPolyWater>();
        return water != null;
    }

    void OnDrawGizmosSelected()
    {
        DrawVortexGizmos();
        DrawCurrentGizmos();
    }

    void DrawVortexGizmos()
    {
        Gizmos.color = new Color(1f, 0.55f, 0.15f, 0.9f);
        VortexFeature[] activeVortices = vortices ?? Array.Empty<VortexFeature>();

        for (int i = 0; i < activeVortices.Length; i++)
        {
            VortexFeature vortex = activeVortices[i];
            DrawCircleXZ(vortex.centerXZ, vortex.radius);

            Vector3 center = new Vector3(vortex.centerXZ.x, transform.position.y, vortex.centerXZ.y);
            float markerLength = Mathf.Max(vortex.radius * 0.35f, 0.35f);
            Vector3 tangent = new Vector3(0f, 0f, Mathf.Sign(vortex.tangentialSpeed)).normalized;
            Gizmos.DrawLine(center, center + (Vector3.right * markerLength));
            Gizmos.DrawLine(center + (Vector3.right * markerLength), center + (Vector3.right * markerLength) + (tangent * markerLength * 0.5f));
        }
    }

    void DrawCurrentGizmos()
    {
        Gizmos.color = new Color(0.25f, 0.95f, 1f, 0.9f);
        CurrentFeature[] activeCurrents = currents ?? Array.Empty<CurrentFeature>();

        for (int i = 0; i < activeCurrents.Length; i++)
        {
            CurrentFeature current = activeCurrents[i];
            Vector2 direction = NormalizeDirection(current.direction);
            Vector2 perpendicular = new Vector2(-direction.y, direction.x);
            Vector2 centerXZ = current.centerXZ;
            float halfWidth = Mathf.Max(current.width * 0.5f, MinFeatureSize);
            float length = Mathf.Max(current.width * 2f, 1f);

            Vector3 center = new Vector3(centerXZ.x, transform.position.y, centerXZ.y);
            Vector3 forward = new Vector3(direction.x, 0f, direction.y) * length;
            Vector3 offset = new Vector3(perpendicular.x, 0f, perpendicular.y) * halfWidth;

            Gizmos.DrawLine(center - forward, center + forward);
            Gizmos.DrawLine(center - forward + offset, center + forward + offset);
            Gizmos.DrawLine(center - forward - offset, center + forward - offset);
        }
    }

    void DrawCircleXZ(Vector2 centerXZ, float radius)
    {
        float safeRadius = Mathf.Max(radius, MinFeatureSize);
        Vector3 previousPoint = new Vector3(centerXZ.x + safeRadius, transform.position.y, centerXZ.y);

        for (int segmentIndex = 1; segmentIndex <= GizmoCircleSegments; segmentIndex++)
        {
            float angle = (segmentIndex / (float)GizmoCircleSegments) * Mathf.PI * 2f;
            Vector3 nextPoint = new Vector3(
                centerXZ.x + (Mathf.Cos(angle) * safeRadius),
                transform.position.y,
                centerXZ.y + (Mathf.Sin(angle) * safeRadius));
            Gizmos.DrawLine(previousPoint, nextPoint);
            previousPoint = nextPoint;
        }
    }

    // Keeps a subtle pool of in-game particles moving through the field so players can read wind direction.
    void UpdateWindParticles()
    {
        if (!showWindParticles || !Application.isPlaying)
        {
            StopWindParticles();
            return;
        }

        ParticleSystem particleSystem = EnsureWindParticleSystem();
        if (particleSystem == null)
        {
            return;
        }

        EnsureWindParticleBuffer();

        float timeSeconds = Time.time;
        float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
        Bounds spawnBounds = GetParticleSpawnBounds();
        bool hasWater = TryResolveWaterSurface(out UrpLowPolyWater water);
        int aliveCount = particleSystem.GetParticles(windParticleBuffer);

        for (int i = 0; i < aliveCount; i++)
        {
            ParticleSystem.Particle particle = windParticleBuffer[i];
            if (ShouldRespawnParticle(particle, spawnBounds, hasWater, water, timeSeconds))
            {
                RespawnParticle(ref particle, spawnBounds, hasWater, water, timeSeconds);
            }
            else
            {
                UpdateParticleVelocity(ref particle, timeSeconds, deltaTime);
            }

            windParticleBuffer[i] = particle;
        }

        int targetCount = Mathf.Min(Mathf.Max((int)MinParticleCount, particleCount), windParticleBuffer.Length);
        while (aliveCount < targetCount)
        {
            ParticleSystem.Particle particle = default;
            RespawnParticle(ref particle, spawnBounds, hasWater, water, timeSeconds);
            windParticleBuffer[aliveCount] = particle;
            aliveCount++;
        }

        particleSystem.SetParticles(windParticleBuffer, aliveCount);
        if (!particleSystem.isPlaying)
        {
            particleSystem.Play();
        }
    }

    // Creates or reuses a child particle system for runtime wind motes.
    ParticleSystem EnsureWindParticleSystem()
    {
        if (windParticleSystem != null)
        {
            ConfigureWindParticleSystem(windParticleSystem);
            return windParticleSystem;
        }

        DestroyLegacyWindIndicatorObject();

        Transform existingChild = transform.Find("Wind Particles");
        if (existingChild != null)
        {
            windParticleSystem = existingChild.GetComponent<ParticleSystem>();
        }

        if (windParticleSystem == null)
        {
            GameObject particleObject = new GameObject("Wind Particles");
            particleObject.transform.SetParent(transform, false);
            windParticleSystem = particleObject.AddComponent<ParticleSystem>();
        }

        ConfigureWindParticleSystem(windParticleSystem);
        return windParticleSystem;
    }

    void ConfigureWindParticleSystem(ParticleSystem particleSystem)
    {
        if (particleSystem == null)
        {
            return;
        }

        var main = particleSystem.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = particleLifetime;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.startColor = particleColor;
        main.maxParticles = Mathf.Max((int)MinParticleCount, particleCount);

        var emission = particleSystem.emission;
        emission.enabled = false;

        var shape = particleSystem.shape;
        shape.enabled = false;

        var sizeOverLifetime = particleSystem.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.separateAxes = false;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
            1f,
            AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.velocityScale = 0f;
            renderer.lengthScale = 1f;
            // Leave screen-size clamping disabled so particles keep their world size
            // and naturally appear smaller when they are farther from the camera.
            renderer.sharedMaterial = GetOrCreateWindParticleMaterial();
        }
    }

    void EnsureWindParticleBuffer()
    {
        int targetSize = Mathf.Max((int)MinParticleCount, particleCount);
        if (windParticleBuffer == null || windParticleBuffer.Length != targetSize)
        {
            windParticleBuffer = new ParticleSystem.Particle[targetSize];
        }
    }

    void StopWindParticles()
    {
        if (windParticleSystem == null)
        {
            return;
        }

        windParticleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    Bounds GetParticleSpawnBounds()
    {
        return new Bounds(transform.position + particleAreaCenterOffset, particleAreaSize);
    }

    bool ShouldRespawnParticle(
        ParticleSystem.Particle particle,
        Bounds spawnBounds,
        bool hasWater,
        UrpLowPolyWater water,
        float timeSeconds)
    {
        if (particle.remainingLifetime <= 0.02f)
        {
            return true;
        }

        Bounds paddedBounds = spawnBounds;
        paddedBounds.Expand(Vector3.Scale(spawnBounds.size, Vector3.one * ParticleBoundsPaddingFactor));
        if (!paddedBounds.Contains(particle.position))
        {
            return true;
        }

        if (!hasWater || water == null)
        {
            return false;
        }

        return water.TryGetSurfaceDataAtWorldPosition(particle.position, timeSeconds, out float waterHeight, out _)
            && particle.position.y < waterHeight;
    }

    void RespawnParticle(
        ref ParticleSystem.Particle particle,
        Bounds spawnBounds,
        bool hasWater,
        UrpLowPolyWater water,
        float timeSeconds)
    {
        Vector3 spawnPosition = GetRandomSpawnPosition(spawnBounds, hasWater, water, timeSeconds);
        particle.position = spawnPosition;
        particle.velocity = GetTargetParticleVelocity(spawnPosition, timeSeconds);
        particle.startLifetime = particleLifetime;
        particle.remainingLifetime = UnityEngine.Random.Range(particleLifetime * 0.45f, particleLifetime);
        particle.startSize = particleSize * UnityEngine.Random.Range(0.8f, 1.2f);
        particle.startColor = particleColor;
    }

    Vector3 GetRandomSpawnPosition(Bounds spawnBounds, bool hasWater, UrpLowPolyWater water, float timeSeconds)
    {
        float x = UnityEngine.Random.Range(spawnBounds.min.x, spawnBounds.max.x);
        float z = UnityEngine.Random.Range(spawnBounds.min.z, spawnBounds.max.z);
        float minY = spawnBounds.min.y;
        float maxY = spawnBounds.max.y;

        if (hasWater
            && water != null
            && water.TryGetSurfaceDataAtWorldPosition(new Vector3(x, spawnBounds.center.y, z), timeSeconds, out float waterHeight, out _))
        {
            minY = Mathf.Max(minY, waterHeight + particleMinHeightAboveWater);
            maxY = Mathf.Max(minY, waterHeight + particleMaxHeightAboveWater);
        }

        float y = UnityEngine.Random.Range(minY, maxY);
        return new Vector3(x, y, z);
    }

    void UpdateParticleVelocity(ref ParticleSystem.Particle particle, float timeSeconds, float deltaTime)
    {
        Vector3 targetVelocity = GetTargetParticleVelocity(particle.position, timeSeconds);
        float blend = 1f - Mathf.Exp(-particleResponsiveness * deltaTime);
        particle.velocity = Vector3.Lerp(particle.velocity, targetVelocity, blend);
    }

    Vector3 GetTargetParticleVelocity(Vector3 worldPosition, float timeSeconds)
    {
        float windStrength = GetWindStrengthMultiplierAtWorldPosition(worldPosition, timeSeconds);
        Vector3 windVelocity = GetWindVelocityAtWorldPosition(worldPosition, timeSeconds) * windStrength;
        return (windVelocity * particleSpeedScale)
            + GetParticleJitter(worldPosition, timeSeconds)
            + (Vector3.up * particleVerticalDrift);
    }

    Vector3 GetParticleJitter(Vector3 worldPosition, float timeSeconds)
    {
        if (particleJitter <= 0f)
        {
            return Vector3.zero;
        }

        float jitterX = Mathf.PerlinNoise(worldPosition.z * 0.13f, timeSeconds * 0.41f) * 2f - 1f;
        float jitterY = Mathf.PerlinNoise(worldPosition.x * 0.09f + 17.3f, timeSeconds * 0.33f) * 2f - 1f;
        float jitterZ = Mathf.PerlinNoise(worldPosition.x * 0.11f + 5.7f, timeSeconds * 0.37f) * 2f - 1f;
        return new Vector3(jitterX, jitterY * 0.25f, jitterZ) * particleJitter;
    }

    // Removes the older editor-style indicator child when scenes still contain it from previous versions.
    void DestroyLegacyWindIndicatorObject()
    {
        Transform indicatorChild = transform.Find("Wind Indicator Particles");
        if (indicatorChild == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(indicatorChild.gameObject);
        }
        else
        {
            DestroyImmediate(indicatorChild.gameObject);
        }
    }

    // Creates a shared runtime material for the subtle in-game wind motes.
    Material GetOrCreateWindParticleMaterial()
    {
        if (windParticleMaterial != null)
        {
            ApplyMaterialAppearance(windParticleMaterial, particleColor);
            return windParticleMaterial;
        }

        Shader shader = FindParticleShader();
        if (shader == null)
        {
            return null;
        }

        windParticleMaterial = new Material(shader)
        {
            name = "Wind Particle Material",
            hideFlags = HideFlags.HideAndDontSave
        };
        ApplyMaterialAppearance(windParticleMaterial, particleColor);
        return windParticleMaterial;
    }

    void ReleaseWindParticleAssets()
    {
        if (windParticleMaterial != null)
        {
            if (Application.isPlaying)
            {
                Destroy(windParticleMaterial);
            }
            else
            {
                DestroyImmediate(windParticleMaterial);
            }

            windParticleMaterial = null;
        }

        if (windParticleTexture != null)
        {
            if (Application.isPlaying)
            {
                Destroy(windParticleTexture);
            }
            else
            {
                DestroyImmediate(windParticleTexture);
            }

            windParticleTexture = null;
        }
    }

    // Tries a short shader fallback list so the same particle texture can render across pipelines.
    Shader FindParticleShader()
    {
        string[] shaderNames =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Sprites/Default",
            "Unlit/Color"
        };

        for (int i = 0; i < shaderNames.Length; i++)
        {
            Shader shader = Shader.Find(shaderNames[i]);
            if (shader != null)
            {
                return shader;
            }
        }

        return null;
    }

    // Colors the shared material and assigns the round particle texture used by the runtime visualizer.
    void ApplyMaterialAppearance(Material material, Color color)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        Texture2D particleTexture = GetOrCreateWindParticleTexture();
        if (particleTexture == null)
        {
            return;
        }

        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", particleTexture);
        }

        if (material.HasProperty("_MainTex"))
        {
            material.SetTexture("_MainTex", particleTexture);
        }
    }

    // Generates a soft circular alpha texture so the particles read as small round motes.
    Texture2D GetOrCreateWindParticleTexture()
    {
        if (windParticleTexture != null)
        {
            return windParticleTexture;
        }

        windParticleTexture = new Texture2D(WindParticleTextureSize, WindParticleTextureSize, TextureFormat.RGBA32, false)
        {
            name = "Wind Particle Texture",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        Color[] pixels = new Color[WindParticleTextureSize * WindParticleTextureSize];
        float radius = (WindParticleTextureSize - 1) * 0.5f;

        for (int y = 0; y < WindParticleTextureSize; y++)
        {
            for (int x = 0; x < WindParticleTextureSize; x++)
            {
                float offsetX = x - radius;
                float offsetY = y - radius;
                float normalizedDistance = Mathf.Sqrt((offsetX * offsetX) + (offsetY * offsetY)) / Mathf.Max(radius, 0.0001f);
                float alpha = 1f - Mathf.SmoothStep(0.7f, 1f, normalizedDistance);
                alpha *= alpha;
                pixels[(y * WindParticleTextureSize) + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        windParticleTexture.SetPixels(pixels);
        windParticleTexture.Apply(false, false);
        return windParticleTexture;
    }
}
