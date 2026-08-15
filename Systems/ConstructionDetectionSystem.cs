using Colossal.IO.AssetDatabase;
using Game;
using Game.Buildings;
using Game.Companies;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ConstructionAnimation.Systems
{
    public partial class ConstructionDetectionSystem : GameSystemBase
    {
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_CompanyRenterQuery;
        private EntityQuery m_SurfaceQuery;
        private EntityQuery m_ConstructionSandAreaQuery;
        private PrefabSystem m_PrefabSystem;
        private TerrainMaterialSystem m_TerrainMaterialSystem;
        private MethodInfo m_ApplyTerrainMaterialBrushMethod;
        private MethodInfo m_ForceUpdateWholeSplatmapMethod;
        private Entity m_DirtTerrainMaterialPrefab = Entity.Null;
        private Entity m_ClearTerrainMaterialPrefab = Entity.Null;
        private Entity m_RectangleBrushPrefab = Entity.Null;
        private bool m_SurfaceLayoutLogged;

        private sealed class FootprintCandidate
        {
            public List<Vector2> Points =
                new List<Vector2>();

            public List<Vector2> ConcaveOutline =
                new List<Vector2>();

            public string PrefabName;

            public float Area;

            public float Compactness;

            public float Score;
        }

        private struct GridBoundaryEdge
        {
            public Vector2Int Start;

            public Vector2Int End;
        }

        private sealed class ConstructionVisual
        {
            public Entity Source = Entity.Null;
            public Entity Proxy = Entity.Null;

            public List<HiddenVanillaSurface> HiddenVanillaSurfaces =
                new List<HiddenVanillaSurface>();

            public HashSet<Entity> LoggedSurfaceSubObjects =
                new HashSet<Entity>();

            public bool SourceSurfaceCaptured;

            public Game.Objects.Surface SourceSurface;

            public List<SuppressedConstructionSandArea> HiddenConstructionSandAreas =
                new List<SuppressedConstructionSandArea>();

            public int ConstructionSandAreaScanAttempts;

            public float NextConstructionSandAreaScanTime;

            public bool TerrainDirtPainted;

            public float3 TerrainPaintPosition;

            public float TerrainPaintSize;

            public float TerrainPaintWidth;

            public float TerrainPaintDepth;

            public float TerrainPaintAngle;

            public float NextTerrainDirtRefreshTime;

            public bool WasSourceHighlighted;

            public bool TerrainDirtRefreshPending;

            public float BuildingHeight = 20f;

            public float3 BuildingSize =
                new float3(
                    20f,
                    20f,
                    20f
                );

            public float3 GeometryPivot =
                float3.zero;

            public List<Vector2> Footprint =
                new List<Vector2>();

            public List<float> FloorBoundaries =
                new List<float>();

            public GameObject ScaffoldRoot;

            public List<GameObject> ScaffoldLevels =
                new List<GameObject>();

            public List<float> ScaffoldLevelBottoms =
                new List<float>();

            public List<float> ScaffoldLevelHeights =
                new List<float>();

            public float ScaffoldHeight;

            public Entity CompanyEntity =
                Entity.Null;

            public Entity CompanyPrefab =
                Entity.Null;

            public string CompanyName;

            public GameObject CompanyBannerRoot;

            public float CompanyBannerRequiredHeight;

            public bool BrandingEligible;

            public float NextBrandingRetryTime;

            public Entity CraneEntity =
                Entity.Null;

            public bool CranePositionLogged;

            public bool CraneVerticalOffsetCaptured;

            public float CraneVerticalOffset;

            public float VisualProgress;

            public float VisualProgressVelocity;

            public byte LastProgress =
                255;

            public bool SeenThisFrame;
        }

        private sealed class HiddenVanillaSurface
        {
            public Entity Entity =
                Entity.Null;

            public Game.Objects.Transform OriginalTransform;

            public bool HadSurface;

            public Game.Objects.Surface Surface;
        }

        private sealed class SuppressedConstructionSandArea
        {
            public Entity Entity =
                Entity.Null;

            public bool HadBatch;
        }

        private sealed class PendingProxyDestroy
        {
            public Entity Entity =
                Entity.Null;

            public int Frames;
        }

        private readonly Dictionary<Entity, ConstructionVisual>
            m_Visuals =
                new Dictionary<Entity, ConstructionVisual>();

        private readonly List<Entity>
            m_RemoveSources =
                new List<Entity>();

        private readonly List<PendingProxyDestroy>
            m_PendingProxyDestroys =
                new List<PendingProxyDestroy>();

        private Material m_ScaffoldMetalMaterial;
        private Material m_ScaffoldDeckMaterial;
        private Material m_ScaffoldTarpMaterial;
        private Material m_CompanyBannerMaterial;
        private Texture2D m_ScaffoldMetalBaseColorTexture;
        private Texture2D m_ScaffoldMetalMaskTexture;
        private Texture2D m_ScaffoldWoodBaseColorTexture;
        private Texture2D m_ScaffoldWoodMaskTexture;
        private Texture2D m_ScaffoldTarpTexture;

        private const int ProxyDestroyDelayFrames =
            4;

        private const float ScaffoldMargin =
            0.15f;

        private const float ScaffoldGeometryClearance =
            0.05f;

        private const float ScaffoldBayWidth =
            3.0f;

        private const float ScaffoldBeamThickness =
            0.10f;

        private const float ScaffoldDeckDepth =
            0.85f;

        private const float ScaffoldDeckThickness =
            0.12f;

        private const float ScaffoldGroundOffset =
            0.05f;

        private const float ProgressSmoothTime =
            0.55f;

        private const float ScaffoldHeightLead =
            0.50f;

        private const float ScaffoldDismantleStart =
            0.92f;

        private const float FallbackTargetFloorHeight =
            2.75f;

        private const float WindowRowTolerance =
            0.35f;

        private const float ScaffoldGridSpacing =
            2.20f;

        private const float ScaffoldGridBeamThickness =
            0.07f;

        private const bool EnableSafetyTarp =
            false;

        private const float CompanyBannerThickness =
            0.06f;

        private const float CompanyBannerMinWidth =
            2.20f;

        private const float CompanyBannerMaxWidth =
            6.50f;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem =
                World.GetOrCreateSystemManaged<PrefabSystem>();

            m_TerrainMaterialSystem =
                World.GetOrCreateSystemManaged<TerrainMaterialSystem>();

            m_BuildingQuery =
                GetEntityQuery(
                    ComponentType.ReadOnly<UnderConstruction>(),
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>()
                );

            m_CompanyRenterQuery =
                GetEntityQuery(
                    ComponentType.ReadOnly<PropertyRenter>(),
                    ComponentType.ReadOnly<PrefabRef>()
                );

            m_SurfaceQuery =
                GetEntityQuery(
                    ComponentType.ReadOnly<Game.Objects.Surface>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>()
                );

            m_ConstructionSandAreaQuery =
                GetEntityQuery(
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>()
                );

            CreateScaffoldMaterials();

            ResolveExperimentalTerrainPaintingApi();

            ModLog.Info(
                "ConstructionAnimation V1.42.29 original square scaffold corners restored and safety tarp removed for performance."
            );
        }

        private void ResolveExperimentalTerrainPaintingApi()
        {
            try
            {
                m_ApplyTerrainMaterialBrushMethod =
                    typeof(TerrainMaterialSystem).GetMethod(
                        "ApplyBrush",
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance
                    );

                m_ForceUpdateWholeSplatmapMethod =
                    typeof(TerrainMaterialSystem).GetMethod(
                        "ForceUpdateWholeSplatmap",
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance
                    );

                EntityQuery materialPrefabQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<TerraformingData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> materialPrefabs =
                    materialPrefabQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int i = 0;
                    i < materialPrefabs.Length;
                    i++
                )
                {
                    Entity prefabEntity =
                        materialPrefabs[i];

                    string prefabName =
                        null;

                    try
                    {
                        prefabName =
                            m_PrefabSystem.GetPrefabName(
                                prefabEntity
                            );
                    }
                    catch
                    {
                    }

                    if (
                        string.Equals(
                            prefabName,
                            "Terrain Material Extra 3",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        m_DirtTerrainMaterialPrefab =
                            prefabEntity;
                    }
                    else if (
                        string.Equals(
                            prefabName,
                            "Terrain Material Clear",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        m_ClearTerrainMaterialPrefab =
                            prefabEntity;
                    }
                }

                EntityQuery brushPrefabQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<BrushData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> brushPrefabs =
                    brushPrefabQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int i = 0;
                    i < brushPrefabs.Length;
                    i++
                )
                {
                    Entity prefabEntity =
                        brushPrefabs[i];

                    string prefabName =
                        null;

                    try
                    {
                        prefabName =
                            m_PrefabSystem.GetPrefabName(
                                prefabEntity
                            );
                    }
                    catch
                    {
                    }

                    if (
                        string.Equals(
                            prefabName,
                            "RectangleBrush",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        m_RectangleBrushPrefab =
                            prefabEntity;

                        break;
                    }
                }

                ModLog.Info(
                    "V1.42.29 terrain painting API resolved: " +
                    "applyMethod=" +
                    (
                        m_ApplyTerrainMaterialBrushMethod != null
                    ) +
                    " forceRefreshMethod=" +
                    (
                        m_ForceUpdateWholeSplatmapMethod != null
                    ) +
                    " dirtPrefab=" +
                    m_DirtTerrainMaterialPrefab.Index +
                    ":" +
                    m_DirtTerrainMaterialPrefab.Version +
                    " clearPrefab=" +
                    m_ClearTerrainMaterialPrefab.Index +
                    ":" +
                    m_ClearTerrainMaterialPrefab.Version +
                    " rectangleBrush=" +
                    m_RectangleBrushPrefab.Index +
                    ":" +
                    m_RectangleBrushPrefab.Version
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.42.29 terrain painting API resolution failed: " +
                    ex
                );
            }
        }

        private void ApplyExperimentalTerrainDirt(
            ConstructionVisual visual,
            bool forceRefresh = false
        )
        {
            if (
                visual == null ||
                (
                    visual.TerrainDirtPainted &&
                    !forceRefresh
                ) ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                m_TerrainMaterialSystem == null ||
                m_ApplyTerrainMaterialBrushMethod == null ||
                m_DirtTerrainMaterialPrefab == Entity.Null ||
                m_RectangleBrushPrefab == Entity.Null ||
                visual.Footprint == null ||
                visual.Footprint.Count < 3
            )
            {
                return;
            }

            try
            {
                Game.Objects.Transform sourceTransform =
                    EntityManager.GetComponentData<Game.Objects.Transform>(
                        visual.Source
                    );

                Vector2 minimum =
                    visual.Footprint[0];

                Vector2 maximum =
                    visual.Footprint[0];

                for (
                    int i = 1;
                    i < visual.Footprint.Count;
                    i++
                )
                {
                    minimum =
                        Vector2.Min(
                            minimum,
                            visual.Footprint[i]
                        );

                    maximum =
                        Vector2.Max(
                            maximum,
                            visual.Footprint[i]
                        );
                }

                float2 footprintSize =
                    new float2(
                        maximum.x -
                        minimum.x,
                        maximum.y -
                        minimum.y
                    );

                try
                {
                    PrefabRef sourcePrefabRef =
                        EntityManager.GetComponentData<PrefabRef>(
                            visual.Source
                        );

                    if (
                        sourcePrefabRef.m_Prefab != Entity.Null &&
                        EntityManager.Exists(
                            sourcePrefabRef.m_Prefab
                        ) &&
                        EntityManager.HasComponent<BuildingData>(
                            sourcePrefabRef.m_Prefab
                        )
                    )
                    {
                        BuildingData buildingData =
                            EntityManager.GetComponentData<BuildingData>(
                                sourcePrefabRef.m_Prefab
                            );

                        float2 lotSize =
                            new float2(
                                buildingData.m_LotSize.x *
                                8f,
                                buildingData.m_LotSize.y *
                                8f
                            );

                        footprintSize =
                            lotSize;
                    }
                }
                catch
                {
                }

                float3 worldPosition =
                    sourceTransform.m_Position;

                quaternion sourceRotation =
                    sourceTransform.m_Rotation;

                Quaternion unityRotation =
                    new Quaternion(
                        sourceRotation.value.x,
                        sourceRotation.value.y,
                        sourceRotation.value.z,
                        sourceRotation.value.w
                    );

                float brushAngle =
                    unityRotation.eulerAngles.y *
                    Mathf.Deg2Rad;

                float3 ownedAreaCenter;

                float2 ownedAreaSize;

                if (
                    TryGetOwnedConstructionAreaBounds(
                        visual,
                        sourceTransform,
                        out ownedAreaCenter,
                        out ownedAreaSize
                    )
                )
                {
                    float3 localAreaCenter3 =
                        math.rotate(
                            math.inverse(
                                sourceTransform.m_Rotation
                            ),
                            ownedAreaCenter -
                            sourceTransform.m_Position
                        );

                    float2 localAreaCenter =
                        new float2(
                            localAreaCenter3.x,
                            localAreaCenter3.z
                        );

                    float2 lotHalfSize =
                        footprintSize *
                        0.5f;

                    float2 areaHalfSize =
                        ownedAreaSize *
                        0.5f;

                    float2 unionMinimum =
                        math.min(
                            -lotHalfSize,
                            localAreaCenter -
                            areaHalfSize
                        );

                    float2 unionMaximum =
                        math.max(
                            lotHalfSize,
                            localAreaCenter +
                            areaHalfSize
                        );

                    float2 unionCenter =
                        (
                            unionMinimum +
                            unionMaximum
                        ) *
                        0.5f;

                    footprintSize =
                        unionMaximum -
                        unionMinimum;

                    worldPosition =
                        sourceTransform.m_Position +
                        math.rotate(
                            sourceTransform.m_Rotation,
                            new float3(
                                unionCenter.x,
                                0f,
                                unionCenter.y
                            )
                        );
                }

                float paintWidth =
                    Mathf.Max(
                        0.5f,
                        footprintSize.x +
                        0.12f
                    );

                float paintDepth =
                    Mathf.Max(
                        0.5f,
                        footprintSize.y +
                        0.12f
                    );

                ApplyTerrainMaterialRectangle(
                    m_DirtTerrainMaterialPrefab,
                    worldPosition,
                    paintWidth,
                    paintDepth,
                    brushAngle
                );

                if (
                    m_ForceUpdateWholeSplatmapMethod != null
                )
                {
                    m_ForceUpdateWholeSplatmapMethod.Invoke(
                        m_TerrainMaterialSystem,
                        null
                    );
                }

                visual.TerrainPaintPosition =
                    worldPosition;

                visual.TerrainPaintSize =
                    Mathf.Max(
                        paintWidth,
                        paintDepth
                    );

                visual.TerrainPaintWidth =
                    paintWidth;

                visual.TerrainPaintDepth =
                    paintDepth;

                visual.TerrainPaintAngle =
                    brushAngle;

                visual.TerrainDirtPainted =
                    true;

                visual.NextTerrainDirtRefreshTime =
                    UnityEngine.Time.unscaledTime +
                    0.75f;

                ModLog.Info(
                    $"V1.42.29 lot-size terrain dirt applied and splatmap refresh requested: " +
                    $"source={visual.Source.Index}:{visual.Source.Version}; " +
                    $"position=({worldPosition.x:0.00},{worldPosition.z:0.00}); " +
                    $"size={paintWidth:0.00}x{paintDepth:0.00}; " +
                    $"angle={brushAngle:0.000}"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.42.29 sand-area terrain dirt application failed: " +
                    ex
                );
            }
        }

        private void ClearExperimentalTerrainDirt(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                !visual.TerrainDirtPainted ||
                m_TerrainMaterialSystem == null ||
                m_ApplyTerrainMaterialBrushMethod == null ||
                m_ClearTerrainMaterialPrefab == Entity.Null ||
                m_RectangleBrushPrefab == Entity.Null
            )
            {
                return;
            }

            try
            {
                ApplyTerrainMaterialRectangle(
                    m_ClearTerrainMaterialPrefab,
                    visual.TerrainPaintPosition,
                    visual.TerrainPaintWidth,
                    visual.TerrainPaintDepth,
                    visual.TerrainPaintAngle
                );

                if (
                    m_ForceUpdateWholeSplatmapMethod != null
                )
                {
                    m_ForceUpdateWholeSplatmapMethod.Invoke(
                        m_TerrainMaterialSystem,
                        null
                    );
                }

                visual.TerrainDirtPainted =
                    false;

                ModLog.Info(
                    "V1.42.29 sand-area terrain dirt cleared and splatmap refresh requested"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.42.29 sand-area terrain dirt clear failed: " +
                    ex
                );
            }
        }

        private void ApplyTerrainMaterialRectangle(
            Entity materialPrefab,
            float3 center,
            float width,
            float depth,
            float angle
        )
        {
            float safeWidth =
                Mathf.Max(
                    0.5f,
                    width
                );

            float safeDepth =
                Mathf.Max(
                    0.5f,
                    depth
                );

            bool extendAlongWidth =
                safeWidth >=
                safeDepth;

            float shortSize =
                Mathf.Min(
                    safeWidth,
                    safeDepth
                );

            float longSize =
                Mathf.Max(
                    safeWidth,
                    safeDepth
                );

            int stampCount =
                Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        longSize /
                        shortSize
                    )
                );

            float centerSpan =
                Mathf.Max(
                    0f,
                    longSize -
                    shortSize
                );

            quaternion rotation =
                quaternion.RotateY(
                    angle
                );

            for (
                int stampIndex = 0;
                stampIndex < stampCount;
                stampIndex++
            )
            {
                float normalized =
                    stampCount == 1
                        ? 0.5f
                        : stampIndex /
                            (float)(
                                stampCount -
                                1
                            );

                float offset =
                    Mathf.Lerp(
                        -centerSpan *
                        0.5f,
                        centerSpan *
                        0.5f,
                        normalized
                    );

                float3 localOffset =
                    extendAlongWidth
                        ? new float3(
                            offset,
                            0f,
                            0f
                        )
                        : new float3(
                            0f,
                            0f,
                            offset
                        );

                float3 stampPosition =
                    center +
                    math.rotate(
                        rotation,
                        localOffset
                    );

                Game.Tools.Brush brush =
                    new Game.Tools.Brush
                    {
                        m_Tool =
                            materialPrefab,

                        m_Position =
                            stampPosition,

                        m_Target =
                            stampPosition,

                        m_Start =
                            stampPosition,

                        m_Angle =
                            angle,

                        m_Size =
                            shortSize,

                        m_Strength =
                            1f,

                        m_Opacity =
                            1f
                    };

                m_ApplyTerrainMaterialBrushMethod.Invoke(
                    m_TerrainMaterialSystem,
                    new object[]
                    {
                        brush,
                        m_RectangleBrushPrefab
                    }
                );
            }
        }

        private bool TryGetOwnedConstructionAreaBounds(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            out float3 worldCenter,
            out float2 areaSize
        )
        {
            worldCenter =
                sourceTransform.m_Position;

            areaSize =
                float2.zero;

            if (
                visual == null ||
                visual.Source == Entity.Null ||
                m_ConstructionSandAreaQuery.IsEmptyIgnoreFilter
            )
            {
                return false;
            }

            quaternion inverseRotation =
                math.inverse(
                    sourceTransform.m_Rotation
                );

            bool foundPosition =
                false;

            float2 minimum =
                new float2(
                    float.MaxValue,
                    float.MaxValue
                );

            float2 maximum =
                new float2(
                    float.MinValue,
                    float.MinValue
                );

            using NativeArray<Entity> ownedAreaCandidates =
                m_ConstructionSandAreaQuery.ToEntityArray(
                    Allocator.Temp
                );

            for (
                int areaIndex = 0;
                areaIndex < ownedAreaCandidates.Length;
                areaIndex++
            )
            {
                Entity areaEntity =
                    ownedAreaCandidates[areaIndex];

                if (
                    areaEntity == Entity.Null ||
                    !EntityManager.Exists(
                        areaEntity
                    ) ||
                    !EntityManager.HasComponent<Owner>(
                        areaEntity
                    ) ||
                    !EntityManager.HasBuffer<Game.Areas.Node>(
                        areaEntity
                    )
                )
                {
                    continue;
                }

                Owner owner =
                    EntityManager.GetComponentData<Owner>(
                        areaEntity
                    );

                if (
                    owner.m_Owner !=
                    visual.Source
                )
                {
                    continue;
                }

                DynamicBuffer<Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<Game.Areas.Node>(
                        areaEntity
                    );

                for (
                    int nodeIndex = 0;
                    nodeIndex < nodes.Length;
                    nodeIndex++
                )
                {
                    float3 nodePosition;

                    if (
                        !TryReadAreaNodePosition(
                            nodes[nodeIndex],
                            out nodePosition
                        )
                    )
                    {
                        continue;
                    }

                    float3 localPosition =
                        math.rotate(
                            inverseRotation,
                            nodePosition -
                            sourceTransform.m_Position
                        );

                    float2 localXZ =
                        new float2(
                            localPosition.x,
                            localPosition.z
                        );

                    minimum =
                        math.min(
                            minimum,
                            localXZ
                        );

                    maximum =
                        math.max(
                            maximum,
                            localXZ
                        );

                    foundPosition =
                        true;
                }
            }

            if (
                !foundPosition
            )
            {
                return false;
            }

            float2 localCenter =
                (
                    minimum +
                    maximum
                ) *
                0.5f;

            float3 rotatedCenter =
                math.rotate(
                    sourceTransform.m_Rotation,
                    new float3(
                        localCenter.x,
                        0f,
                        localCenter.y
                    )
                );

            worldCenter =
                sourceTransform.m_Position +
                rotatedCenter;

            areaSize =
                new float2(
                    Mathf.Max(
                        0.5f,
                        maximum.x -
                        minimum.x
                    ),
                    Mathf.Max(
                        0.5f,
                        maximum.y -
                        minimum.y
                    )
                );

            return true;
        }

        private static bool TryReadAreaNodePosition(
            Game.Areas.Node node,
            out float3 position
        )
        {
            position =
                float3.zero;

            try
            {
                FieldInfo[] fields =
                    typeof(Game.Areas.Node).GetFields(
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance
                    );

                object boxedNode =
                    node;

                for (
                    int fieldIndex = 0;
                    fieldIndex < fields.Length;
                    fieldIndex++
                )
                {
                    FieldInfo field =
                        fields[fieldIndex];

                    if (
                        field.Name.IndexOf(
                            "position",
                            StringComparison.OrdinalIgnoreCase
                        ) < 0
                    )
                    {
                        continue;
                    }

                    object value =
                        field.GetValue(
                            boxedNode
                        );

                    if (
                        value is float3
                    )
                    {
                        position =
                            (float3)value;

                        return true;
                    }

                    if (
                        value is Vector3
                    )
                    {
                        Vector3 vector =
                            (Vector3)value;

                        position =
                            new float3(
                                vector.x,
                                vector.y,
                                vector.z
                            );

                        return true;
                    }

                    if (
                        value is float2
                    )
                    {
                        float2 vector =
                            (float2)value;

                        position =
                            new float3(
                                vector.x,
                                0f,
                                vector.y
                            );

                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void LogTerrainBrushDetails()
        {
            try
            {
                Assembly gameAssembly =
                    typeof(UnderConstruction).Assembly;

                string[] typeNames =
                {
                    "Game.Tools.Brush",
                    "Game.Tools.BrushDefinition",
                    "Game.Prefabs.BrushData",
                    "Game.Prefabs.TerraformingData",
                    "Game.Prefabs.TerraformingBrushData",
                    "Game.Prefabs.TerrainMaterialType"
                };

                BindingFlags flags =
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly;

                for (
                    int typeIndex = 0;
                    typeIndex < typeNames.Length;
                    typeIndex++
                )
                {
                    Type type =
                        gameAssembly.GetType(
                            typeNames[typeIndex],
                            false
                        );

                    if (
                        type == null
                    )
                    {
                        ModLog.Info(
                            "V1.42.19.1 terrain detail type not found: " +
                            typeNames[typeIndex]
                        );

                        continue;
                    }

                    ModLog.Info(
                        "V1.42.19.1 terrain detail type: " +
                        "type=" +
                        type.FullName +
                        " isValueType=" +
                        type.IsValueType +
                        " isEnum=" +
                        type.IsEnum
                    );

                    if (
                        type.IsEnum
                    )
                    {
                        string[] enumNames =
                            Enum.GetNames(
                                type
                            );

                        Array enumValues =
                            Enum.GetValues(
                                type
                            );

                        for (
                            int enumIndex = 0;
                            enumIndex < enumNames.Length;
                            enumIndex++
                        )
                        {
                            ModLog.Info(
                                "V1.42.19.1 terrain enum value: " +
                                "type=" +
                                type.FullName +
                                " name=" +
                                enumNames[enumIndex] +
                                " value=" +
                                Convert.ToInt64(
                                    enumValues.GetValue(
                                        enumIndex
                                    )
                                )
                            );
                        }
                    }

                    FieldInfo[] fields =
                        type.GetFields(
                            flags
                        );

                    for (
                        int fieldIndex = 0;
                        fieldIndex < fields.Length;
                        fieldIndex++
                    )
                    {
                        FieldInfo field =
                            fields[fieldIndex];

                        ModLog.Info(
                            "V1.42.19.1 terrain detail field: " +
                            "declaringType=" +
                            type.FullName +
                            " field=" +
                            field.Name +
                            " fieldType=" +
                            field.FieldType.FullName +
                            " static=" +
                            field.IsStatic
                        );
                    }

                    PropertyInfo[] properties =
                        type.GetProperties(
                            flags
                        );

                    for (
                        int propertyIndex = 0;
                        propertyIndex < properties.Length;
                        propertyIndex++
                    )
                    {
                        PropertyInfo property =
                            properties[propertyIndex];

                        ModLog.Info(
                            "V1.42.19.1 terrain detail property: " +
                            "declaringType=" +
                            type.FullName +
                            " property=" +
                            property.Name +
                            " propertyType=" +
                            property.PropertyType.FullName +
                            " canRead=" +
                            property.CanRead +
                            " canWrite=" +
                            property.CanWrite
                        );
                    }

                    ConstructorInfo[] constructors =
                        type.GetConstructors(
                            flags
                        );

                    for (
                        int constructorIndex = 0;
                        constructorIndex < constructors.Length;
                        constructorIndex++
                    )
                    {
                        ParameterInfo[] parameters =
                            constructors[constructorIndex].GetParameters();

                        string signature =
                            string.Empty;

                        for (
                            int parameterIndex = 0;
                            parameterIndex < parameters.Length;
                            parameterIndex++
                        )
                        {
                            if (
                                parameterIndex > 0
                            )
                            {
                                signature +=
                                    ", ";
                            }

                            signature +=
                                parameters[parameterIndex].ParameterType.FullName +
                                " " +
                                parameters[parameterIndex].Name;
                        }

                        ModLog.Info(
                            "V1.42.19.1 terrain detail constructor: " +
                            "type=" +
                            type.FullName +
                            " parameters=(" +
                            signature +
                            ")"
                        );
                    }
                }

                EntityQuery materialPrefabQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<TerraformingData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> materialPrefabs =
                    materialPrefabQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int prefabIndex = 0;
                    prefabIndex < materialPrefabs.Length;
                    prefabIndex++
                )
                {
                    Entity prefabEntity =
                        materialPrefabs[prefabIndex];

                    string prefabName =
                        null;

                    try
                    {
                        prefabName =
                            m_PrefabSystem.GetPrefabName(
                                prefabEntity
                            );
                    }
                    catch
                    {
                    }

                    if (
                        string.IsNullOrWhiteSpace(
                            prefabName
                        )
                    )
                    {
                        continue;
                    }

                    ModLog.Info(
                        "V1.42.19.1 terraforming prefab: " +
                        "entity=" +
                        prefabEntity.Index +
                        ":" +
                        prefabEntity.Version +
                        " name=" +
                        prefabName
                    );
                }

                EntityQuery brushPrefabQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<BrushData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> brushPrefabs =
                    brushPrefabQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int brushIndex = 0;
                    brushIndex < brushPrefabs.Length;
                    brushIndex++
                )
                {
                    Entity brushEntity =
                        brushPrefabs[brushIndex];

                    string brushName =
                        null;

                    try
                    {
                        brushName =
                            m_PrefabSystem.GetPrefabName(
                                brushEntity
                            );
                    }
                    catch
                    {
                    }

                    BrushData brushData =
                        EntityManager.GetComponentData<BrushData>(
                            brushEntity
                        );

                    ModLog.Info(
                        "V1.42.19.2 brush prefab: " +
                        "entity=" +
                        brushEntity.Index +
                        ":" +
                        brushEntity.Version +
                        " name=" +
                        (
                            string.IsNullOrWhiteSpace(
                                brushName
                            )
                                ? "<unnamed>"
                                : brushName
                        ) +
                        " priority=" +
                        brushData.m_Priority +
                        " resolution=" +
                        brushData.m_Resolution
                    );
                }

                ModLog.Info(
                    "V1.42.19.2 terrain detail scan completed: " +
                    "terraformingPrefabs=" +
                    materialPrefabs.Length +
                    " brushPrefabs=" +
                    brushPrefabs.Length
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.42.19.1 terrain detail scan failed: " +
                    ex
                );
            }
        }

        private void LogTerrainPaintingApi()
        {
            try
            {
                Assembly gameAssembly =
                    typeof(UnderConstruction).Assembly;

                Type[] types;

                try
                {
                    types =
                        gameAssembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types =
                        ex.Types;
                }

                string[] targetTypeNames =
                {
                    "Game.Tools.TerrainToolSystem",
                    "Game.Simulation.TerrainSystem",
                    "Game.Rendering.TerrainMaterialSystem",
                    "Colossal.Terrain.TerrainSystem",
                    "Colossal.Terrain.TerrainMaterialSystem"
                };

                BindingFlags flags =
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly;

                int typeMatches =
                    0;

                int memberMatches =
                    0;

                for (
                    int typeIndex = 0;
                    typeIndex < types.Length;
                    typeIndex++
                )
                {
                    Type type =
                        types[typeIndex];

                    if (
                        type == null ||
                        string.IsNullOrEmpty(
                            type.FullName
                        )
                    )
                    {
                        continue;
                    }

                    bool isTarget =
                        false;

                    for (
                        int targetIndex = 0;
                        targetIndex < targetTypeNames.Length;
                        targetIndex++
                    )
                    {
                        if (
                            string.Equals(
                                type.FullName,
                                targetTypeNames[targetIndex],
                                StringComparison.Ordinal
                            )
                        )
                        {
                            isTarget =
                                true;

                            break;
                        }
                    }

                    if (
                        !isTarget
                    )
                    {
                        continue;
                    }

                    typeMatches++;

                    ModLog.Info(
                        "V1.42.19 terrain API type: " +
                        "type=" +
                        type.FullName +
                        " baseType=" +
                        (
                            type.BaseType != null
                                ? type.BaseType.FullName
                                : "<none>"
                        )
                    );

                    FieldInfo[] fields =
                        type.GetFields(
                            flags
                        );

                    for (
                        int fieldIndex = 0;
                        fieldIndex < fields.Length;
                        fieldIndex++
                    )
                    {
                        FieldInfo field =
                            fields[fieldIndex];

                        if (
                            !IsTerrainPaintingMemberName(
                                field.Name
                            )
                        )
                        {
                            continue;
                        }

                        memberMatches++;

                        ModLog.Info(
                            "V1.42.19 terrain API field: " +
                            "declaringType=" +
                            type.FullName +
                            " field=" +
                            field.Name +
                            " fieldType=" +
                            field.FieldType.FullName +
                            " static=" +
                            field.IsStatic
                        );
                    }

                    PropertyInfo[] properties =
                        type.GetProperties(
                            flags
                        );

                    for (
                        int propertyIndex = 0;
                        propertyIndex < properties.Length;
                        propertyIndex++
                    )
                    {
                        PropertyInfo property =
                            properties[propertyIndex];

                        if (
                            !IsTerrainPaintingMemberName(
                                property.Name
                            )
                        )
                        {
                            continue;
                        }

                        memberMatches++;

                        ModLog.Info(
                            "V1.42.19 terrain API property: " +
                            "declaringType=" +
                            type.FullName +
                            " property=" +
                            property.Name +
                            " propertyType=" +
                            property.PropertyType.FullName +
                            " canRead=" +
                            property.CanRead +
                            " canWrite=" +
                            property.CanWrite
                        );
                    }

                    MethodInfo[] methods =
                        type.GetMethods(
                            flags
                        );

                    for (
                        int methodIndex = 0;
                        methodIndex < methods.Length;
                        methodIndex++
                    )
                    {
                        MethodInfo method =
                            methods[methodIndex];

                        if (
                            !IsTerrainPaintingMemberName(
                                method.Name
                            )
                        )
                        {
                            continue;
                        }

                        ParameterInfo[] parameters =
                            method.GetParameters();

                        string signature =
                            string.Empty;

                        for (
                            int parameterIndex = 0;
                            parameterIndex < parameters.Length;
                            parameterIndex++
                        )
                        {
                            if (
                                parameterIndex > 0
                            )
                            {
                                signature +=
                                    ", ";
                            }

                            signature +=
                                parameters[parameterIndex].ParameterType.FullName +
                                " " +
                                parameters[parameterIndex].Name;
                        }

                        memberMatches++;

                        ModLog.Info(
                            "V1.42.19 terrain API method: " +
                            "declaringType=" +
                            type.FullName +
                            " method=" +
                            method.Name +
                            " returnType=" +
                            method.ReturnType.FullName +
                            " parameters=(" +
                            signature +
                            ") static=" +
                            method.IsStatic
                        );
                    }
                }

                ModLog.Info(
                    "V1.42.19 terrain API scan completed: " +
                    "types=" +
                    typeMatches +
                    " members=" +
                    memberMatches
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.42.19 terrain API scan failed: " +
                    ex
                );
            }
        }

        private bool IsTerrainPaintingMemberName(
            string memberName
        )
        {
            if (
                string.IsNullOrEmpty(
                    memberName
                )
            )
            {
                return false;
            }

            return
                memberName.IndexOf(
                    "Splat",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0 ||
                memberName.IndexOf(
                    "Brush",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0 ||
                memberName.IndexOf(
                    "Material",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0 ||
                memberName.IndexOf(
                    "Apply",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0 ||
                memberName.IndexOf(
                    "Texture",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0;
        }

        private void LogConstructionSurfaceDeclarations()
        {
            try
            {
                Assembly gameAssembly =
                    typeof(UnderConstruction).Assembly;

                Type[] types;

                try
                {
                    types =
                        gameAssembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types =
                        ex.Types;
                }

                int matchCount =
                    0;

                BindingFlags flags =
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly;

                for (
                    int typeIndex = 0;
                    typeIndex < types.Length;
                    typeIndex++
                )
                {
                    Type type =
                        types[typeIndex];

                    if (
                        type == null
                    )
                    {
                        continue;
                    }

                    FieldInfo[] fields;

                    try
                    {
                        fields =
                            type.GetFields(
                                flags
                            );
                    }
                    catch
                    {
                        fields =
                            Array.Empty<FieldInfo>();
                    }

                    for (
                        int fieldIndex = 0;
                        fieldIndex < fields.Length;
                        fieldIndex++
                    )
                    {
                        FieldInfo field =
                            fields[fieldIndex];

                        if (
                            field == null ||
                            field.Name.IndexOf(
                                "ConstructionSurface",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                        {
                            continue;
                        }

                        matchCount++;

                        ModLog.Info(
                            "V1.42.17 construction surface field: " +
                            "assembly=" +
                            gameAssembly.GetName().Name +
                            " declaringType=" +
                            type.FullName +
                            " field=" +
                            field.Name +
                            " fieldType=" +
                            field.FieldType.FullName +
                            " static=" +
                            field.IsStatic
                        );
                    }

                    PropertyInfo[] properties;

                    try
                    {
                        properties =
                            type.GetProperties(
                                flags
                            );
                    }
                    catch
                    {
                        properties =
                            Array.Empty<PropertyInfo>();
                    }

                    for (
                        int propertyIndex = 0;
                        propertyIndex < properties.Length;
                        propertyIndex++
                    )
                    {
                        PropertyInfo property =
                            properties[propertyIndex];

                        if (
                            property == null ||
                            property.Name.IndexOf(
                                "ConstructionSurface",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                        {
                            continue;
                        }

                        matchCount++;

                        ModLog.Info(
                            "V1.42.17 construction surface property: " +
                            "assembly=" +
                            gameAssembly.GetName().Name +
                            " declaringType=" +
                            type.FullName +
                            " property=" +
                            property.Name +
                            " propertyType=" +
                            property.PropertyType.FullName
                        );
                    }
                }

                ModLog.Info(
                    "V1.42.17 construction surface reflection scan completed: " +
                    "matches=" +
                    matchCount +
                    " types=" +
                    types.Length
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.42.17 construction surface reflection scan failed: " +
                    ex
                );
            }
        }

        protected override void OnUpdate()
        {
            ProcessPendingProxyDestroys();

            foreach (
                ConstructionVisual visual
                in m_Visuals.Values
            )
            {
                visual.SeenThisFrame =
                    false;
            }

            using NativeArray<Entity> buildings =
                m_BuildingQuery.ToEntityArray(
                    Allocator.Temp
                );

            for (
                int i = 0;
                i < buildings.Length;
                i++
            )
            {
                Entity source =
                    buildings[i];

                if (
                    source == Entity.Null ||
                    !EntityManager.Exists(
                        source
                    )
                )
                {
                    continue;
                }

                ConstructionVisual visual;

                if (
                    !m_Visuals.TryGetValue(
                        source,
                        out visual
                    )
                )
                {
                    visual =
                        CreateConstructionVisual(
                            source
                        );

                    if (
                        visual == null
                    )
                    {
                        continue;
                    }

                    m_Visuals.Add(
                        source,
                        visual
                    );
                }

                visual.SeenThisFrame =
                    true;

                UpdateConstructionVisual(
                    visual
                );
            }

            m_RemoveSources.Clear();

            foreach (
                KeyValuePair<Entity, ConstructionVisual> pair
                in m_Visuals
            )
            {
                if (
                    !pair.Value.SeenThisFrame
                )
                {
                    m_RemoveSources.Add(
                        pair.Key
                    );
                }
            }

            for (
                int i = 0;
                i < m_RemoveSources.Count;
                i++
            )
            {
                Entity source =
                    m_RemoveSources[i];

                ConstructionVisual visual;

                if (
                    !m_Visuals.TryGetValue(
                        source,
                        out visual
                    )
                )
                {
                    continue;
                }

                DestroyConstructionVisual(
                    visual
                );

                m_Visuals.Remove(
                    source
                );
            }

            if (
                m_RemoveSources.Count > 0
            )
            {
                foreach (
                    ConstructionVisual remainingVisual
                    in m_Visuals.Values
                )
                {
                    ApplyExperimentalTerrainDirt(
                        remainingVisual,
                        true
                    );
                }
            }
        }

        private ConstructionVisual CreateConstructionVisual(
            Entity source
        )
        {
            try
            {
                ConstructionVisual visual =
                    new ConstructionVisual();

                visual.Source =
                    source;

                HideOwnedConstructionSandAreas(
                    visual
                );

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        source
                    );

                UnderConstruction construction =
                    EntityManager.GetComponentData<UnderConstruction>(
                        source
                    );

                visual.VisualProgress =
                    math.saturate(
                        construction.m_Progress /
                        100f
                    );

                visual.VisualProgressVelocity =
                    0f;

                ReadBuildingGeometry(
                    prefabRef.m_Prefab,
                    visual
                );

                AnalyseBuildingMeshes(
                    prefabRef.m_Prefab,
                    visual
                );

                if (
                    visual.Footprint == null ||
                    visual.Footprint.Count < 3
                )
                {
                    visual.Footprint =
                        CreateFallbackFootprint(
                            visual
                        );
                }

                if (
                    visual.FloorBoundaries == null ||
                    visual.FloorBoundaries.Count < 2
                )
                {
                    visual.FloorBoundaries =
                        CreateFallbackFloors(
                            visual.BuildingHeight
                        );
                }

                CreateNativeProxy(
                    visual,
                    prefabRef
                );

                CreateScaffold(
                    visual
                );

                ApplyExperimentalTerrainDirt(
                    visual
                );

                visual.BrandingEligible =
                    IsBrandingEligibleBuilding(
                        prefabRef.m_Prefab
                    );

                if (
                    visual.BrandingEligible
                )
                {
                    CreateCompanyBanner(
                        visual
                    );
                }

                ModLog.Info(
                    $"V1.42.5 construction added " +
                    $"{source.Index}:{source.Version}; " +
                    $"progress={construction.m_Progress}%; " +
                    $"visualProgress={visual.VisualProgress:0.000}; " +
                    $"floors={visual.FloorBoundaries.Count - 1}; " +
                    $"height={visual.BuildingHeight:0.00}m"
                );

                return visual;
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    $"V1.42.5 CreateConstructionVisual failed: {ex}"
                );

                return null;
            }
        }

        private bool IsBrandingEligibleBuilding(
            Entity buildingPrefab
        )
        {
            if (
                buildingPrefab == Entity.Null ||
                !EntityManager.Exists(
                    buildingPrefab
                ) ||
                !EntityManager.HasComponent<BuildingPropertyData>(
                    buildingPrefab
                )
            )
            {
                return false;
            }

            try
            {
                BuildingPropertyData properties =
                    EntityManager.GetComponentData<BuildingPropertyData>(
                        buildingPrefab
                    );

                return
                    properties.m_ResidentialProperties ==
                    0;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 branding classification failed: " +
                    $"{ex.GetType().Name}"
                );

                return false;
            }
        }

        private bool TryGetCompanyAtBuilding(
            Entity building,
            out Entity companyEntity,
            out Entity companyPrefab,
            out string companyName
        )
        {
            companyEntity =
                Entity.Null;

            companyPrefab =
                Entity.Null;

            companyName =
                null;

            if (
                building == Entity.Null ||
                !EntityManager.Exists(
                    building
                )
            )
            {
                return false;
            }

            if (
                EntityManager.HasBuffer<Renter>(
                    building
                )
            )
            {
                DynamicBuffer<Renter> renters =
                    EntityManager.GetBuffer<Renter>(
                        building
                    );

            for (
                int i = 0;
                i < renters.Length;
                i++
            )
            {
                Entity renter =
                    renters[i].m_Renter;

                if (
                    renter == Entity.Null ||
                    !EntityManager.Exists(
                        renter
                    ) ||
                    !EntityManager.HasComponent<PrefabRef>(
                        renter
                    )
                )
                {
                    continue;
                }

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        renter
                    );

                Entity prefab =
                    prefabRef.m_Prefab;

                if (
                    prefab == Entity.Null ||
                    !EntityManager.Exists(
                        prefab
                    ) ||
                    !EntityManager.HasComponent<CompanyData>(
                        prefab
                    )
                )
                {
                    continue;
                }

                string prefabName =
                    null;

                try
                {
                    prefabName =
                        m_PrefabSystem.GetPrefabName(
                            prefab
                        );
                }
                catch
                {
                }

                companyEntity =
                    renter;

                companyPrefab =
                    prefab;

                companyName =
                    string.IsNullOrWhiteSpace(
                        prefabName
                    )
                        ? "Company"
                        : prefabName;

                ModLog.Info(
                    $"V1.42.5 COMPANY FOUND " +
                    $"building={building.Index}:{building.Version} " +
                    $"company={companyEntity.Index}:{companyEntity.Version} " +
                    $"prefab={companyPrefab.Index}:{companyPrefab.Version} " +
                    $"name={companyName}"
                );

                return true;
            }

            }

            using NativeArray<Entity> renterEntities =
                m_CompanyRenterQuery.ToEntityArray(
                    Allocator.Temp
                );

            for (
                int i = 0;
                i < renterEntities.Length;
                i++
            )
            {
                Entity renter =
                    renterEntities[i];

                if (
                    renter == Entity.Null ||
                    !EntityManager.Exists(
                        renter
                    )
                )
                {
                    continue;
                }

                PropertyRenter propertyRenter =
                    EntityManager.GetComponentData<PropertyRenter>(
                        renter
                    );

                if (
                    propertyRenter.m_Property !=
                    building
                )
                {
                    continue;
                }

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        renter
                    );

                Entity prefab =
                    prefabRef.m_Prefab;

                if (
                    prefab == Entity.Null ||
                    !EntityManager.Exists(
                        prefab
                    ) ||
                    !EntityManager.HasComponent<CompanyData>(
                        prefab
                    )
                )
                {
                    continue;
                }

                string prefabName =
                    null;

                try
                {
                    prefabName =
                        m_PrefabSystem.GetPrefabName(
                            prefab
                        );
                }
                catch
                {
                }

                companyEntity =
                    renter;

                companyPrefab =
                    prefab;

                companyName =
                    string.IsNullOrWhiteSpace(
                        prefabName
                    )
                        ? "Company"
                        : prefabName;

                ModLog.Info(
                    $"V1.42.5 COMPANY FOUND REVERSE " +
                    $"building={building.Index}:{building.Version} " +
                    $"company={companyEntity.Index}:{companyEntity.Version} " +
                    $"prefab={companyPrefab.Index}:{companyPrefab.Version} " +
                    $"name={companyName}"
                );

                return true;
            }

            return false;
        }

        private void CreateCompanyBanner(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.ScaffoldRoot == null
            )
            {
                return;
            }

            Entity companyEntity;
            Entity companyPrefab;
            string companyName;

            if (
                !TryGetCompanyAtBuilding(
                    visual.Source,
                    out companyEntity,
                    out companyPrefab,
                    out companyName
                )
            )
            {
                visual.NextBrandingRetryTime =
                    UnityEngine.Time.unscaledTime +
                    1f;

                return;
            }

            visual.CompanyEntity =
                companyEntity;

            visual.CompanyPrefab =
                companyPrefab;

            visual.CompanyName =
                companyName;

            List<Vector2> outline =
                CreateScaffoldOutline(
                    visual.Footprint
                );

            if (
                outline == null ||
                outline.Count < 2
            )
            {
                return;
            }

            int bestEdge =
                0;

            float bestLength =
                0f;

            for (
                int i = 0;
                i < outline.Count;
                i++
            )
            {
                Vector2 a =
                    outline[i];

                Vector2 b =
                    outline[
                        (
                            i +
                            1
                        ) %
                        outline.Count
                    ];

                float length =
                    (
                        b -
                        a
                    ).magnitude;

                if (
                    length >
                    bestLength
                )
                {
                    bestLength =
                        length;

                    bestEdge =
                        i;
                }
            }

            Vector2 edgeA =
                outline[
                    bestEdge
                ];

            Vector2 edgeB =
                outline[
                    (
                        bestEdge +
                        1
                    ) %
                    outline.Count
                ];

            Vector2 edgeDirection =
                (
                    edgeB -
                    edgeA
                ).normalized;

            if (
                edgeDirection.sqrMagnitude <
                0.001f
            )
            {
                return;
            }

            Vector2 outward =
                new Vector2(
                    edgeDirection.y,
                    -edgeDirection.x
                );

            float bannerWidth =
                Mathf.Clamp(
                    bestLength *
                    0.55f,
                    CompanyBannerMinWidth,
                    CompanyBannerMaxWidth
                );

            float bannerHeight =
                Mathf.Clamp(
                    bannerWidth *
                    0.28f,
                    0.90f,
                    1.80f
                );

            float preferredY =
                visual.FloorBoundaries != null &&
                visual.FloorBoundaries.Count > 1
                    ? visual.FloorBoundaries[1] *
                      0.70f
                    : 2f;

            float bannerY =
                Mathf.Clamp(
                    preferredY,
                    1.50f,
                    Mathf.Max(
                        1.50f,
                        visual.BuildingHeight *
                        0.45f
                    )
                );

            Vector2 midpoint =
                (
                    edgeA +
                    edgeB
                ) *
                0.5f;

            Vector2 local2D =
                midpoint +
                outward *
                0.10f;

            GameObject bannerRoot =
                new GameObject(
                    $"CompanyBanner_{visual.Source.Index}"
                );

            bannerRoot.hideFlags =
                HideFlags.DontSave;

            bannerRoot.transform.SetParent(
                visual.ScaffoldRoot.transform,
                false
            );

            bannerRoot.transform.localPosition =
                new Vector3(
                    local2D.x,
                    bannerY,
                    local2D.y
                );

            Vector3 outward3 =
                new Vector3(
                    outward.x,
                    0f,
                    outward.y
                );

            bannerRoot.transform.localRotation =
                Quaternion.LookRotation(
                    outward3,
                    Vector3.up
                );

            GameObject panel =
                GameObject.CreatePrimitive(
                    PrimitiveType.Cube
                );

            panel.name =
                "CompanyBannerPanel";

            panel.hideFlags =
                HideFlags.DontSave;

            panel.transform.SetParent(
                bannerRoot.transform,
                false
            );

            panel.transform.localPosition =
                Vector3.zero;

            panel.transform.localRotation =
                Quaternion.identity;

            panel.transform.localScale =
                new Vector3(
                    bannerWidth,
                    bannerHeight,
                    CompanyBannerThickness
                );

            MeshRenderer panelRenderer =
                panel.GetComponent<MeshRenderer>();

            if (
                panelRenderer != null &&
                m_CompanyBannerMaterial != null
            )
            {
                panelRenderer.sharedMaterial =
                    m_CompanyBannerMaterial;
            }

            RemovePrimitiveCollider(
                panel
            );

            visual.CompanyBannerRoot =
                bannerRoot;

            visual.NextBrandingRetryTime =
                float.PositiveInfinity;

            visual.CompanyBannerRequiredHeight =
                float.PositiveInfinity;

            bannerRoot.SetActive(
                false
            );

            ModLog.Info(
                $"V1.42.5 company branding candidate found; " +
                $"placeholder kept hidden " +
                $"building={visual.Source.Index}:{visual.Source.Version} " +
                $"company={companyEntity.Index}:{companyEntity.Version} " +
                $"companyPrefab={companyPrefab.Index}:{companyPrefab.Version} " +
                $"name={companyName}"
            );

            DumpCompanyBrandCandidates(
                companyPrefab
            );
        }

        private void UpdateCompanyBannerVisibility(
            ConstructionVisual visual,
            float scaffoldVisibleHeight,
            bool constructionActive
        )
        {
            if (
                visual == null ||
                visual.CompanyBannerRoot == null
            )
            {
                return;
            }

            bool visible =
                constructionActive &&
                scaffoldVisibleHeight >=
                visual.CompanyBannerRequiredHeight;

            if (
                visual.CompanyBannerRoot.activeSelf !=
                visible
            )
            {
                visual.CompanyBannerRoot.SetActive(
                    visible
                );
            }
        }

        private void DumpCompanyBrandCandidates(
            Entity companyPrefab
        )
        {
            if (
                companyPrefab == Entity.Null ||
                !EntityManager.Exists(
                    companyPrefab
                )
            )
            {
                return;
            }

            try
            {
                string prefabName =
                    m_PrefabSystem.GetPrefabName(
                        companyPrefab
                    );

                ModLog.Info(
                    $"V1.42.5 BRAND prefab={prefabName}"
                );

                if (
                    EntityManager.HasBuffer<CompanyBrandElement>(
                        companyPrefab
                    )
                )
                {
                    DynamicBuffer<CompanyBrandElement> brands =
                        EntityManager.GetBuffer<CompanyBrandElement>(
                            companyPrefab
                        );

                    ModLog.Info(
                        $"V1.42.5 BRAND CompanyBrandElement count=" +
                        $"{brands.Length}"
                    );

                    for (
                        int i = 0;
                        i < brands.Length;
                        i++
                    )
                    {
                        object boxed =
                            brands[i];

                        FieldInfo[] fields =
                            boxed
                                .GetType()
                                .GetFields(
                                    BindingFlags.Instance |
                                    BindingFlags.Public |
                                    BindingFlags.NonPublic
                                );

                        for (
                            int j = 0;
                            j < fields.Length;
                            j++
                        )
                        {
                            object value =
                                fields[j].GetValue(
                                    boxed
                                );

                            ModLog.Info(
                                $"V1.42.5 BRAND element[{i}] " +
                                $"{fields[j].Name}=" +
                                $"{(value == null ? "null" : value.ToString())}"
                            );
                        }
                    }
                }
                else
                {
                    ModLog.Info(
                        "V1.42.5 BRAND company prefab has no " +
                        "CompanyBrandElement buffer."
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 BRAND diagnostic failed: " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );
            }
        }

        private void ReadBuildingGeometry(
            Entity prefab,
            ConstructionVisual visual
        )
        {
            visual.BuildingHeight =
                20f;

            visual.BuildingSize =
                new float3(
                    20f,
                    20f,
                    20f
                );

            visual.GeometryPivot =
                float3.zero;

            try
            {
                if (
                    !EntityManager.HasComponent<ObjectGeometryData>(
                        prefab
                    )
                )
                {
                    return;
                }

                ObjectGeometryData geometry =
                    EntityManager.GetComponentData<ObjectGeometryData>(
                        prefab
                    );

                visual.BuildingSize =
                    geometry.m_Size;

                visual.GeometryPivot =
                    geometry.m_Pivot;

                if (
                    geometry.m_Size.y >
                    0.5f &&
                    geometry.m_Size.y <
                    1000f
                )
                {
                    visual.BuildingHeight =
                        geometry.m_Size.y;
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 geometry read failed: " +
                    $"{ex.GetType().Name}"
                );
            }
        }

        private void AnalyseBuildingMeshes(
            Entity buildingPrefab,
            ConstructionVisual visual
        )
        {
            List<FootprintCandidate> footprintCandidates =
                new List<FootprintCandidate>();

            List<float> windowRowCandidates =
                new List<float>();

            float globalMinY =
                float.MaxValue;

            try
            {
                if (
                    !EntityManager.HasBuffer<SubMesh>(
                        buildingPrefab
                    )
                )
                {
                    return;
                }

                DynamicBuffer<SubMesh> subMeshes =
                    EntityManager.GetBuffer<SubMesh>(
                        buildingPrefab
                    );

                for (
                    int subIndex = 0;
                    subIndex < subMeshes.Length;
                    subIndex++
                )
                {
                    SubMesh subMesh =
                        subMeshes[
                            subIndex
                        ];

                    Entity renderPrefabEntity =
                        subMesh.m_SubMesh;

                    if (
                        renderPrefabEntity ==
                        Entity.Null ||
                        !EntityManager.Exists(
                            renderPrefabEntity
                        )
                    )
                    {
                        continue;
                    }

                    PrefabBase managedPrefab;

                    try
                    {
                        managedPrefab =
                            m_PrefabSystem
                                .GetPrefab<PrefabBase>(
                                    renderPrefabEntity
                                );
                    }
                    catch
                    {
                        continue;
                    }

                    if (
                        managedPrefab ==
                        null
                    )
                    {
                        continue;
                    }

                    GeometryAsset geometryAsset =
                        GetGeometryAsset(
                            managedPrefab
                        );

                    if (
                        geometryAsset ==
                        null
                    )
                    {
                        continue;
                    }

                    try
                    {
                        Mesh[] meshes =
                            geometryAsset.ObtainMeshes(
                                true
                            );

                        if (
                            meshes == null ||
                            meshes.Length ==
                            0
                        )
                        {
                            continue;
                        }

                        Mesh mainMesh =
                            meshes[0];

                        if (
                            mainMesh !=
                            null
                        )
                        {
                            List<Vector2> candidatePoints =
                                new List<Vector2>();

                            Vector3[] vertices =
                                mainMesh.vertices;

                            for (
                                int vertexIndex = 0;
                                vertexIndex < vertices.Length;
                                vertexIndex++
                            )
                            {
                                Vector3 v =
                                    vertices[
                                        vertexIndex
                                    ];

                                float3 local =
                                    new float3(
                                        v.x,
                                        v.y,
                                        v.z
                                    );

                                local =
                                    math.rotate(
                                        subMesh.m_Rotation,
                                        local
                                    );

                                local +=
                                    subMesh.m_Position;

                                candidatePoints.Add(
                                    new Vector2(
                                        local.x,
                                        local.z
                                    )
                                );

                                globalMinY =
                                    math.min(
                                        globalMinY,
                                        local.y
                                    );
                            }

                            string renderPrefabName =
                                null;

                            try
                            {
                                renderPrefabName =
                                    m_PrefabSystem.GetPrefabName(
                                        renderPrefabEntity
                                    );
                            }
                            catch
                            {
                            }

                            AddFootprintCandidate(
                                footprintCandidates,
                                candidatePoints,
                                renderPrefabName,
                                CreateRasterizedMeshFootprint(
                                    candidatePoints,
                                    mainMesh.triangles
                                )
                            );
                        }

                        if (
                            meshes.Length >=
                            3 &&
                            meshes[2] !=
                            null
                        )
                        {
                            ExtractWindowComponentCenters(
                                meshes[2],
                                subMesh,
                                windowRowCandidates
                            );
                        }
                        else if (
                            meshes.Length >=
                            2 &&
                            meshes[1] !=
                            null
                        )
                        {
                            ExtractWindowComponentCenters(
                                meshes[1],
                                subMesh,
                                windowRowCandidates
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLog.Info(
                            $"V1.42.5 mesh analysis failed: " +
                            $"{ex.GetType().Name}"
                        );
                    }
                    finally
                    {
                        try
                        {
                            geometryAsset.ReleaseMeshes();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 AnalyseBuildingMeshes failed: " +
                    $"{ex.GetType().Name}"
                );
            }

            FootprintCandidate selectedCandidate =
                SelectFootprintCandidate(
                    footprintCandidates
                );

            if (
                selectedCandidate != null
            )
            {
                bool useConcaveOutline =
                    selectedCandidate.ConcaveOutline != null &&
                    selectedCandidate.ConcaveOutline.Count >= 3;

                bool useBoundingRectangle =
                    !useConcaveOutline &&
                    selectedCandidate.Compactness >=
                    0.85f;

                if (
                    useConcaveOutline
                )
                {
                    visual.Footprint =
                        selectedCandidate.ConcaveOutline;
                }
                else if (
                    useBoundingRectangle
                )
                {
                    visual.Footprint =
                        CreateBoundingFootprint(
                            selectedCandidate.Points
                        );
                }
                else
                {
                    visual.Footprint =
                        SimplifyHull(
                            CalculateConvexHull(
                                selectedCandidate.Points
                            )
                        );
                }

                ModLog.Info(
                    $"V1.42.5 footprint selected " +
                    $"prefab={selectedCandidate.PrefabName}; " +
                    $"area={selectedCandidate.Area:0.00}; " +
                    $"compactness={selectedCandidate.Compactness:0.000}; " +
                    $"score={selectedCandidate.Score:0.00}; " +
                    $"candidates={footprintCandidates.Count}; " +
                    $"mode={(useConcaveOutline ? "concave-grid" : (useBoundingRectangle ? "rectangle" : "hull"))}"
                );
            }

            if (
                globalMinY !=
                float.MaxValue &&
                windowRowCandidates.Count >
                0
            )
            {
                List<float> normalizedRows =
                    new List<float>();

                for (
                    int i = 0;
                    i < windowRowCandidates.Count;
                    i++
                )
                {
                    float normalized =
                        windowRowCandidates[i] -
                        globalMinY;

                    if (
                        normalized >
                        0.25f &&
                        normalized <
                        visual.BuildingHeight +
                        1f
                    )
                    {
                        normalizedRows.Add(
                            normalized
                        );
                    }
                }

                List<float> rows =
                    ClusterHeights(
                        normalizedRows,
                        WindowRowTolerance
                    );

                if (
                    rows.Count >=
                    2
                )
                {
                    visual.FloorBoundaries =
                        BuildFloorBoundariesFromWindowRows(
                            rows,
                            visual.BuildingHeight
                        );

                    LogDetectedFloors(
                        visual,
                        rows
                    );
                }
            }
        }

        private void ExtractWindowComponentCenters(
            Mesh mesh,
            SubMesh subMesh,
            List<float> output
        )
        {
            if (
                mesh ==
                null ||
                output ==
                null
            )
            {
                return;
            }

            Vector3[] vertices;
            int[] triangles;

            try
            {
                vertices =
                    mesh.vertices;

                triangles =
                    mesh.triangles;
            }
            catch
            {
                return;
            }

            if (
                vertices ==
                null ||
                vertices.Length ==
                0
            )
            {
                return;
            }

            if (
                triangles ==
                null ||
                triangles.Length <
                3
            )
            {
                for (
                    int i = 0;
                    i < vertices.Length;
                    i++
                )
                {
                    float3 local =
                        new float3(
                            vertices[i].x,
                            vertices[i].y,
                            vertices[i].z
                        );

                    local =
                        math.rotate(
                            subMesh.m_Rotation,
                            local
                        );

                    local +=
                        subMesh.m_Position;

                    output.Add(
                        local.y
                    );
                }

                return;
            }

            int vertexCount =
                vertices.Length;

            int[] parent =
                new int[
                    vertexCount
                ];

            for (
                int i = 0;
                i < vertexCount;
                i++
            )
            {
                parent[i] =
                    i;
            }

            for (
                int i = 0;
                i + 2 < triangles.Length;
                i += 3
            )
            {
                int a =
                    triangles[i];

                int b =
                    triangles[
                        i +
                        1
                    ];

                int c =
                    triangles[
                        i +
                        2
                    ];

                if (
                    a < 0 ||
                    a >= vertexCount ||
                    b < 0 ||
                    b >= vertexCount ||
                    c < 0 ||
                    c >= vertexCount
                )
                {
                    continue;
                }

                Union(
                    parent,
                    a,
                    b
                );

                Union(
                    parent,
                    b,
                    c
                );

                Union(
                    parent,
                    c,
                    a
                );
            }

            Dictionary<int, float> minY =
                new Dictionary<int, float>();

            Dictionary<int, float> maxY =
                new Dictionary<int, float>();

            Dictionary<int, int> counts =
                new Dictionary<int, int>();

            for (
                int i = 0;
                i < vertexCount;
                i++
            )
            {
                int root =
                    FindRoot(
                        parent,
                        i
                    );

                float3 local =
                    new float3(
                        vertices[i].x,
                        vertices[i].y,
                        vertices[i].z
                    );

                local =
                    math.rotate(
                        subMesh.m_Rotation,
                        local
                    );

                local +=
                    subMesh.m_Position;

                if (
                    !minY.ContainsKey(
                        root
                    )
                )
                {
                    minY[root] =
                        local.y;

                    maxY[root] =
                        local.y;

                    counts[root] =
                        1;
                }
                else
                {
                    minY[root] =
                        math.min(
                            minY[root],
                            local.y
                        );

                    maxY[root] =
                        math.max(
                            maxY[root],
                            local.y
                        );

                    counts[root]++;
                }
            }

            foreach (
                KeyValuePair<int, float> pair
                in minY
            )
            {
                int root =
                    pair.Key;

                float min =
                    pair.Value;

                float max =
                    maxY[root];

                float componentHeight =
                    max -
                    min;

                int count =
                    counts[root];

                if (
                    count <
                    3 ||
                    componentHeight <
                    0.10f ||
                    componentHeight >
                    4.5f
                )
                {
                    continue;
                }

                output.Add(
                    (
                        min +
                        max
                    ) *
                    0.5f
                );
            }
        }

        private void AddFootprintCandidate(
            List<FootprintCandidate> candidates,
            List<Vector2> points,
            string prefabName,
            List<Vector2> concaveOutline
        )
        {
            if (
                candidates == null ||
                points == null ||
                points.Count < 3
            )
            {
                return;
            }

            List<Vector2> hull =
                CalculateConvexHull(
                    points
                );

            if (
                hull == null ||
                hull.Count < 3
            )
            {
                return;
            }

            float minX =
                float.MaxValue;

            float maxX =
                float.MinValue;

            float minZ =
                float.MaxValue;

            float maxZ =
                float.MinValue;

            for (
                int i = 0;
                i < hull.Count;
                i++
            )
            {
                minX =
                    Mathf.Min(
                        minX,
                        hull[i].x
                    );

                maxX =
                    Mathf.Max(
                        maxX,
                        hull[i].x
                    );

                minZ =
                    Mathf.Min(
                        minZ,
                        hull[i].y
                    );

                maxZ =
                    Mathf.Max(
                        maxZ,
                        hull[i].y
                    );
            }

            float width =
                maxX -
                minX;

            float depth =
                maxZ -
                minZ;

            float boundingArea =
                width *
                depth;

            if (
                width < 0.75f ||
                depth < 0.75f ||
                boundingArea < 1f
            )
            {
                return;
            }

            float area =
                CalculatePolygonArea(
                    hull
                );

            float compactness =
                area /
                Mathf.Max(
                    boundingArea,
                    0.001f
                );

            float aspect =
                Mathf.Max(
                    width /
                    Mathf.Max(
                        depth,
                        0.001f
                    ),
                    depth /
                    Mathf.Max(
                        width,
                        0.001f
                    )
                );

            if (
                compactness < 0.12f ||
                aspect > 8f
            )
            {
                ModLog.Info(
                    $"V1.42.5 footprint rejected " +
                    $"prefab={prefabName}; " +
                    $"area={area:0.00}; " +
                    $"compactness={compactness:0.000}; " +
                    $"aspect={aspect:0.00}"
                );

                return;
            }

            FootprintCandidate candidate =
                new FootprintCandidate();

            candidate.Points =
                points;

            if (
                concaveOutline != null &&
                concaveOutline.Count >= 3
            )
            {
                candidate.ConcaveOutline =
                    concaveOutline;

                candidate.Area =
                    CalculatePolygonArea(
                        concaveOutline
                    );

                candidate.Compactness =
                    candidate.Area /
                    Mathf.Max(
                        boundingArea,
                        0.001f
                    );
            }

            candidate.PrefabName =
                prefabName;

            if (
                candidate.Area <= 0f
            )
            {
                candidate.Area =
                    area;
            }

            if (
                candidate.Compactness <= 0f
            )
            {
                candidate.Compactness =
                    compactness;
            }

            candidate.Score =
                candidate.Area *
                candidate.Compactness *
                Mathf.Sqrt(
                    Mathf.Max(
                        1,
                        points.Count
                    )
                );

            candidates.Add(
                candidate
            );
        }

        private static FootprintCandidate SelectFootprintCandidate(
            List<FootprintCandidate> candidates
        )
        {
            if (
                candidates == null ||
                candidates.Count == 0
            )
            {
                return null;
            }

            FootprintCandidate best =
                candidates[0];

            for (
                int i = 1;
                i < candidates.Count;
                i++
            )
            {
                if (
                    candidates[i].Score >
                    best.Score
                )
                {
                    best =
                        candidates[i];
                }
            }

            return best;
        }

        private static float CalculatePolygonArea(
            List<Vector2> polygon
        )
        {
            if (
                polygon == null ||
                polygon.Count < 3
            )
            {
                return 0f;
            }

            double twiceArea =
                0.0;

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 a =
                    polygon[i];

                Vector2 b =
                    polygon[
                        (
                            i +
                            1
                        ) %
                        polygon.Count
                    ];

                twiceArea +=
                    (
                        double
                    )a.x *
                    b.y -
                    (
                        double
                    )a.y *
                    b.x;
            }

            return
                Mathf.Abs(
                    (
                        float
                    )twiceArea
                ) *
                0.5f;
        }

        private static int FindRoot(
            int[] parent,
            int value
        )
        {
            while (
                parent[value] !=
                value
            )
            {
                parent[value] =
                    parent[
                        parent[
                            value
                        ]
                    ];

                value =
                    parent[value];
            }

            return value;
        }

        private static void Union(
            int[] parent,
            int a,
            int b
        )
        {
            int rootA =
                FindRoot(
                    parent,
                    a
                );

            int rootB =
                FindRoot(
                    parent,
                    b
                );

            if (
                rootA !=
                rootB
            )
            {
                parent[rootB] =
                    rootA;
            }
        }

        private static List<float> ClusterHeights(
            List<float> input,
            float tolerance
        )
        {
            List<float> result =
                new List<float>();

            if (
                input ==
                null ||
                input.Count ==
                0
            )
            {
                return result;
            }

            input.Sort();

            float sum =
                input[0];

            int count =
                1;

            float currentMean =
                input[0];

            for (
                int i = 1;
                i < input.Count;
                i++
            )
            {
                float value =
                    input[i];

                if (
                    math.abs(
                        value -
                        currentMean
                    ) <=
                    tolerance
                )
                {
                    sum +=
                        value;

                    count++;

                    currentMean =
                        sum /
                        count;
                }
                else
                {
                    result.Add(
                        currentMean
                    );

                    sum =
                        value;

                    count =
                        1;

                    currentMean =
                        value;
                }
            }

            result.Add(
                currentMean
            );

            return result;
        }

        private List<float> BuildFloorBoundariesFromWindowRows(
            List<float> rows,
            float buildingHeight
        )
        {
            if (
                rows ==
                null ||
                rows.Count <
                2
            )
            {
                return
                    CreateFallbackFloors(
                        buildingHeight
                    );
            }

            rows.Sort();

            List<float> boundaries =
                new List<float>();

            boundaries.Add(
                0f
            );

            for (
                int i = 0;
                i < rows.Count -
                1;
                i++
            )
            {
                float boundary =
                    (
                        rows[i] +
                        rows[
                            i +
                            1
                        ]
                    ) *
                    0.5f;

                if (
                    boundary >
                    0.5f &&
                    boundary <
                    buildingHeight -
                    0.25f
                )
                {
                    boundaries.Add(
                        boundary
                    );
                }
            }

            boundaries.Add(
                buildingHeight
            );

            List<float> cleaned =
                new List<float>();

            cleaned.Add(
                boundaries[0]
            );

            for (
                int i = 1;
                i < boundaries.Count;
                i++
            )
            {
                float difference =
                    boundaries[i] -
                    cleaned[
                        cleaned.Count -
                        1
                    ];

                if (
                    difference >=
                    1.75f ||
                    i ==
                    boundaries.Count -
                    1
                )
                {
                    cleaned.Add(
                        boundaries[i]
                    );
                }
            }

            if (
                cleaned.Count <
                2
            )
            {
                return
                    CreateFallbackFloors(
                        buildingHeight
                    );
            }

            for (
                int i = 0;
                i < cleaned.Count -
                1;
                i++
            )
            {
                float height =
                    cleaned[
                        i +
                        1
                    ] -
                    cleaned[i];

                if (
                    height <
                    1.75f ||
                    height >
                    6.5f
                )
                {
                    return
                        CreateFallbackFloors(
                            buildingHeight
                        );
                }
            }

            return cleaned;
        }

        private List<float> CreateFallbackFloors(
            float buildingHeight
        )
        {
            List<float> result =
                new List<float>();

            int floorCount =
                Math.Max(
                    1,
                    Mathf.RoundToInt(
                        buildingHeight /
                        FallbackTargetFloorHeight
                    )
                );

            float actualFloorHeight =
                buildingHeight /
                floorCount;

            for (
                int i = 0;
                i <= floorCount;
                i++
            )
            {
                result.Add(
                    actualFloorHeight *
                    i
                );
            }

            ModLog.Info(
                $"V1.42.5 floor fallback: " +
                $"height={buildingHeight:0.00}m, " +
                $"floors={floorCount}, " +
                $"each={actualFloorHeight:0.00}m"
            );

            return result;
        }

        private void LogDetectedFloors(
            ConstructionVisual visual,
            List<float> rows
        )
        {
            string rowText =
                "";

            for (
                int i = 0;
                i < rows.Count;
                i++
            )
            {
                if (
                    i >
                    0
                )
                {
                    rowText +=
                        ", ";
                }

                rowText +=
                    rows[i].ToString(
                        "0.00"
                    );
            }

            string floorText =
                "";

            for (
                int i = 0;
                i < visual.FloorBoundaries.Count;
                i++
            )
            {
                if (
                    i >
                    0
                )
                {
                    floorText +=
                        ", ";
                }

                floorText +=
                    visual
                        .FloorBoundaries[i]
                        .ToString(
                            "0.00"
                        );
            }

            ModLog.Info(
                $"V1.42.5 detected window rows: " +
                $"[{rowText}]"
            );

            ModLog.Info(
                $"V1.42.5 floor boundaries: " +
                $"[{floorText}]"
            );
        }

        private List<Vector2> CreateFallbackFootprint(
            ConstructionVisual visual
        )
        {
            float halfWidth =
                visual.BuildingSize.x *
                0.5f;

            float halfDepth =
                visual.BuildingSize.z *
                0.5f;

            return new List<Vector2>
            {
                new Vector2(
                    -halfWidth,
                    -halfDepth
                ),

                new Vector2(
                    halfWidth,
                    -halfDepth
                ),

                new Vector2(
                    halfWidth,
                    halfDepth
                ),

                new Vector2(
                    -halfWidth,
                    halfDepth
                )
            };
        }

        private static List<Vector2> CreateBoundingFootprint(
            List<Vector2> points
        )
        {
            if (
                points == null ||
                points.Count < 3
            )
            {
                return new List<Vector2>();
            }

            float minX =
                float.MaxValue;

            float maxX =
                float.MinValue;

            float minZ =
                float.MaxValue;

            float maxZ =
                float.MinValue;

            for (
                int i = 0;
                i < points.Count;
                i++
            )
            {
                minX =
                    Mathf.Min(
                        minX,
                        points[i].x
                    );

                maxX =
                    Mathf.Max(
                        maxX,
                        points[i].x
                    );

                minZ =
                    Mathf.Min(
                        minZ,
                        points[i].y
                    );

                maxZ =
                    Mathf.Max(
                        maxZ,
                        points[i].y
                    );
            }

            return new List<Vector2>
            {
                new Vector2(
                    minX,
                    minZ
                ),

                new Vector2(
                    maxX,
                    minZ
                ),

                new Vector2(
                    maxX,
                    maxZ
                ),

                new Vector2(
                    minX,
                    maxZ
                )
            };
        }

        private static List<Vector2> CreateRasterizedMeshFootprint(
            List<Vector2> vertices,
            int[] triangles
        )
        {
            List<Vector2> empty =
                new List<Vector2>();

            if (
                vertices == null ||
                vertices.Count < 3 ||
                triangles == null ||
                triangles.Length < 3
            )
            {
                return empty;
            }

            float minX =
                float.MaxValue;

            float maxX =
                float.MinValue;

            float minZ =
                float.MaxValue;

            float maxZ =
                float.MinValue;

            for (
                int vertexIndex = 0;
                vertexIndex < vertices.Count;
                vertexIndex++
            )
            {
                Vector2 vertex =
                    vertices[vertexIndex];

                minX =
                    Mathf.Min(
                        minX,
                        vertex.x
                    );

                maxX =
                    Mathf.Max(
                        maxX,
                        vertex.x
                    );

                minZ =
                    Mathf.Min(
                        minZ,
                        vertex.y
                    );

                maxZ =
                    Mathf.Max(
                        maxZ,
                        vertex.y
                    );
            }

            float width =
                maxX -
                minX;

            float depth =
                maxZ -
                minZ;

            if (
                width < 0.5f ||
                depth < 0.5f
            )
            {
                return empty;
            }

            float cellSize =
                Mathf.Max(
                    0.50f,
                    Mathf.Max(
                        width,
                        depth
                    ) /
                    160f
                );

            int cellsX =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        width /
                        cellSize
                    ),
                    1,
                    192
                );

            int cellsZ =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        depth /
                        cellSize
                    ),
                    1,
                    192
                );

            bool[,] occupied =
                new bool[
                    cellsX,
                    cellsZ
                ];

            for (
                int triangleIndex = 0;
                triangleIndex + 2 < triangles.Length;
                triangleIndex += 3
            )
            {
                int indexA =
                    triangles[triangleIndex];

                int indexB =
                    triangles[
                        triangleIndex +
                        1
                    ];

                int indexC =
                    triangles[
                        triangleIndex +
                        2
                    ];

                if (
                    indexA < 0 ||
                    indexB < 0 ||
                    indexC < 0 ||
                    indexA >= vertices.Count ||
                    indexB >= vertices.Count ||
                    indexC >= vertices.Count
                )
                {
                    continue;
                }

                Vector2 a =
                    vertices[indexA];

                Vector2 b =
                    vertices[indexB];

                Vector2 c =
                    vertices[indexC];

                float projectedArea =
                    Mathf.Abs(
                        Cross(
                            a,
                            b,
                            c
                        )
                    );

                if (
                    projectedArea < 0.01f
                )
                {
                    continue;
                }

                int startX =
                    Mathf.Clamp(
                        Mathf.FloorToInt(
                            (
                                Mathf.Min(
                                    a.x,
                                    Mathf.Min(
                                        b.x,
                                        c.x
                                    )
                                ) -
                                minX
                            ) /
                            cellSize
                        ),
                        0,
                        cellsX -
                        1
                    );

                int endX =
                    Mathf.Clamp(
                        Mathf.FloorToInt(
                            (
                                Mathf.Max(
                                    a.x,
                                    Mathf.Max(
                                        b.x,
                                        c.x
                                    )
                                ) -
                                minX
                            ) /
                            cellSize
                        ),
                        0,
                        cellsX -
                        1
                    );

                int startZ =
                    Mathf.Clamp(
                        Mathf.FloorToInt(
                            (
                                Mathf.Min(
                                    a.y,
                                    Mathf.Min(
                                        b.y,
                                        c.y
                                    )
                                ) -
                                minZ
                            ) /
                            cellSize
                        ),
                        0,
                        cellsZ -
                        1
                    );

                int endZ =
                    Mathf.Clamp(
                        Mathf.FloorToInt(
                            (
                                Mathf.Max(
                                    a.y,
                                    Mathf.Max(
                                        b.y,
                                        c.y
                                    )
                                ) -
                                minZ
                            ) /
                            cellSize
                        ),
                        0,
                        cellsZ -
                        1
                    );

                for (
                    int cellX = startX;
                    cellX <= endX;
                    cellX++
                )
                {
                    for (
                        int cellZ = startZ;
                        cellZ <= endZ;
                        cellZ++
                    )
                    {
                        Vector2 sample =
                            new Vector2(
                                minX +
                                (
                                    cellX +
                                    0.5f
                                ) *
                                cellSize,
                                minZ +
                                (
                                    cellZ +
                                    0.5f
                                ) *
                                cellSize
                            );

                        if (
                            IsPointInsideTriangle(
                                sample,
                                a,
                                b,
                                c
                            )
                        )
                        {
                            occupied[
                                cellX,
                                cellZ
                            ] =
                                true;
                        }
                    }
                }
            }

            List<GridBoundaryEdge> edges =
                new List<GridBoundaryEdge>();

            for (
                int cellX = 0;
                cellX < cellsX;
                cellX++
            )
            {
                for (
                    int cellZ = 0;
                    cellZ < cellsZ;
                    cellZ++
                )
                {
                    if (
                        !occupied[
                            cellX,
                            cellZ
                        ]
                    )
                    {
                        continue;
                    }

                    if (
                        cellZ == 0 ||
                        !occupied[
                            cellX,
                            cellZ -
                            1
                        ]
                    )
                    {
                        AddGridBoundaryEdge(
                            edges,
                            cellX,
                            cellZ,
                            cellX +
                            1,
                            cellZ
                        );
                    }

                    if (
                        cellX == cellsX - 1 ||
                        !occupied[
                            cellX +
                            1,
                            cellZ
                        ]
                    )
                    {
                        AddGridBoundaryEdge(
                            edges,
                            cellX +
                            1,
                            cellZ,
                            cellX +
                            1,
                            cellZ +
                            1
                        );
                    }

                    if (
                        cellZ == cellsZ - 1 ||
                        !occupied[
                            cellX,
                            cellZ +
                            1
                        ]
                    )
                    {
                        AddGridBoundaryEdge(
                            edges,
                            cellX +
                            1,
                            cellZ +
                            1,
                            cellX,
                            cellZ +
                            1
                        );
                    }

                    if (
                        cellX == 0 ||
                        !occupied[
                            cellX -
                            1,
                            cellZ
                        ]
                    )
                    {
                        AddGridBoundaryEdge(
                            edges,
                            cellX,
                            cellZ +
                            1,
                            cellX,
                            cellZ
                        );
                    }
                }
            }

            List<Vector2> bestLoop =
                new List<Vector2>();

            float bestArea =
                0f;

            bool[] used =
                new bool[
                    edges.Count
                ];

            for (
                int edgeIndex = 0;
                edgeIndex < edges.Count;
                edgeIndex++
            )
            {
                if (
                    used[edgeIndex]
                )
                {
                    continue;
                }

                List<Vector2Int> gridLoop =
                    TraceGridBoundaryLoop(
                        edges,
                        used,
                        edgeIndex
                    );

                if (
                    gridLoop == null ||
                    gridLoop.Count < 3
                )
                {
                    continue;
                }

                List<Vector2> worldLoop =
                    new List<Vector2>();

                for (
                    int loopIndex = 0;
                    loopIndex < gridLoop.Count;
                    loopIndex++
                )
                {
                    worldLoop.Add(
                        new Vector2(
                            minX +
                            gridLoop[loopIndex].x *
                            cellSize,
                            minZ +
                            gridLoop[loopIndex].y *
                            cellSize
                        )
                    );
                }

                worldLoop =
                    SimplifyGridFootprint(
                        worldLoop
                    );

                float loopArea =
                    CalculatePolygonArea(
                        worldLoop
                    );

                if (
                    loopArea > bestArea
                )
                {
                    bestArea =
                        loopArea;

                    bestLoop =
                        worldLoop;
                }
            }

            return bestLoop;
        }

        private static bool IsPointInsideTriangle(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c
        )
        {
            float crossA =
                Cross(
                    a,
                    b,
                    point
                );

            float crossB =
                Cross(
                    b,
                    c,
                    point
                );

            float crossC =
                Cross(
                    c,
                    a,
                    point
                );

            bool hasNegative =
                crossA < -0.0001f ||
                crossB < -0.0001f ||
                crossC < -0.0001f;

            bool hasPositive =
                crossA > 0.0001f ||
                crossB > 0.0001f ||
                crossC > 0.0001f;

            return !(
                hasNegative &&
                hasPositive
            );
        }

        private static void AddGridBoundaryEdge(
            List<GridBoundaryEdge> edges,
            int startX,
            int startY,
            int endX,
            int endY
        )
        {
            GridBoundaryEdge edge =
                new GridBoundaryEdge();

            edge.Start =
                new Vector2Int(
                    startX,
                    startY
                );

            edge.End =
                new Vector2Int(
                    endX,
                    endY
                );

            edges.Add(
                edge
            );
        }

        private static List<Vector2Int> TraceGridBoundaryLoop(
            List<GridBoundaryEdge> edges,
            bool[] used,
            int firstEdgeIndex
        )
        {
            List<Vector2Int> result =
                new List<Vector2Int>();

            GridBoundaryEdge firstEdge =
                edges[firstEdgeIndex];

            Vector2Int start =
                firstEdge.Start;

            Vector2Int current =
                firstEdge.End;

            used[firstEdgeIndex] =
                true;

            result.Add(
                start
            );

            int safety =
                edges.Count +
                1;

            while (
                current != start &&
                safety > 0
            )
            {
                result.Add(
                    current
                );

                int nextEdgeIndex =
                    -1;

                for (
                    int edgeIndex = 0;
                    edgeIndex < edges.Count;
                    edgeIndex++
                )
                {
                    if (
                        !used[edgeIndex] &&
                        edges[edgeIndex].Start ==
                        current
                    )
                    {
                        nextEdgeIndex =
                            edgeIndex;

                        break;
                    }
                }

                if (
                    nextEdgeIndex < 0
                )
                {
                    return new List<Vector2Int>();
                }

                used[nextEdgeIndex] =
                    true;

                current =
                    edges[nextEdgeIndex].End;

                safety--;
            }

            if (
                current != start
            )
            {
                return new List<Vector2Int>();
            }

            return result;
        }

        private static List<Vector2> CreateChamferedScaffoldOutline(
            List<Vector2> outline,
            float diagonalLength
        )
        {
            if (
                outline == null ||
                outline.Count < 3 ||
                diagonalLength <= 0f
            )
            {
                return outline;
            }

            List<Vector2> result =
                new List<Vector2>(
                    outline.Count * 2
                );

            for (
                int i = 0;
                i < outline.Count;
                i++
            )
            {
                Vector2 previous =
                    outline[
                        (
                            i - 1 +
                            outline.Count
                        ) %
                        outline.Count
                    ];

                Vector2 current =
                    outline[i];

                Vector2 next =
                    outline[
                        (
                            i + 1
                        ) %
                        outline.Count
                    ];

                Vector2 incoming =
                    current -
                    previous;

                Vector2 outgoing =
                    next -
                    current;

                float incomingLength =
                    incoming.magnitude;

                float outgoingLength =
                    outgoing.magnitude;

                if (
                    incomingLength < 0.08f ||
                    outgoingLength < 0.08f
                )
                {
                    result.Add(
                        current
                    );

                    continue;
                }

                float distance =
                    Mathf.Min(
                        diagonalLength,
                        Mathf.Min(
                            incomingLength * 0.20f,
                            outgoingLength * 0.20f
                        )
                    );

                if (
                    distance < 0.03f
                )
                {
                    result.Add(
                        current
                    );

                    continue;
                }

                Vector2 curveStart =
                    current -
                    incoming /
                    incomingLength *
                    distance;

                Vector2 curveEnd =
                    current +
                    outgoing /
                    outgoingLength *
                    distance;

                result.Add(
                    curveStart
                );

                result.Add(
                    curveEnd
                );
            }

            return
                result.Count >= 3
                    ? result
                    : outline;
        }

        private static List<Vector2> SimplifyGridFootprint(
            List<Vector2> outline
        )
        {
            if (
                outline == null ||
                outline.Count < 4
            )
            {
                return outline;
            }

            List<Vector2> result =
                new List<Vector2>(
                    outline
                );

            bool changed =
                true;

            while (
                changed &&
                result.Count > 3
            )
            {
                changed =
                    false;

                for (
                    int index = 0;
                    index < result.Count;
                    index++
                )
                {
                    Vector2 previous =
                        result[
                            (
                                index -
                                1 +
                                result.Count
                            ) %
                            result.Count
                        ];

                    Vector2 current =
                        result[index];

                    Vector2 next =
                        result[
                            (
                                index +
                                1
                            ) %
                            result.Count
                        ];

                    Vector2 directionA =
                        current -
                        previous;

                    Vector2 directionB =
                        next -
                        current;

                    if (
                        directionA.sqrMagnitude < 0.0001f ||
                        directionB.sqrMagnitude < 0.0001f ||
                        Mathf.Abs(
                            directionA.x *
                            directionB.y -
                            directionA.y *
                            directionB.x
                        ) < 0.0001f
                    )
                    {
                        result.RemoveAt(
                            index
                        );

                        changed =
                            true;

                        break;
                    }
                }
            }

            return result;
        }

        private static List<Vector2> CalculateConvexHull(
            List<Vector2> input
        )
        {
            List<Vector2> points =
                new List<Vector2>(
                    input
                );

            points.Sort(
                delegate (
                    Vector2 a,
                    Vector2 b
                )
                {
                    int x =
                        a.x.CompareTo(
                            b.x
                        );

                    if (
                        x !=
                        0
                    )
                    {
                        return x;
                    }

                    return
                        a.y.CompareTo(
                            b.y
                        );
                }
            );

            List<Vector2> unique =
                new List<Vector2>();

            for (
                int i = 0;
                i < points.Count;
                i++
            )
            {
                if (
                    unique.Count ==
                    0 ||
                    Vector2.SqrMagnitude(
                        points[i] -
                        unique[
                            unique.Count -
                            1
                        ]
                    ) >
                    0.0025f
                )
                {
                    unique.Add(
                        points[i]
                    );
                }
            }

            if (
                unique.Count <=
                3
            )
            {
                return unique;
            }

            List<Vector2> lower =
                new List<Vector2>();

            for (
                int i = 0;
                i < unique.Count;
                i++
            )
            {
                Vector2 p =
                    unique[i];

                while (
                    lower.Count >=
                    2 &&
                    Cross(
                        lower[
                            lower.Count -
                            2
                        ],
                        lower[
                            lower.Count -
                            1
                        ],
                        p
                    ) <=
                    0f
                )
                {
                    lower.RemoveAt(
                        lower.Count -
                        1
                    );
                }

                lower.Add(
                    p
                );
            }

            List<Vector2> upper =
                new List<Vector2>();

            for (
                int i = unique.Count -
                    1;
                i >=
                0;
                i--
            )
            {
                Vector2 p =
                    unique[i];

                while (
                    upper.Count >=
                    2 &&
                    Cross(
                        upper[
                            upper.Count -
                            2
                        ],
                        upper[
                            upper.Count -
                            1
                        ],
                        p
                    ) <=
                    0f
                )
                {
                    upper.RemoveAt(
                        upper.Count -
                        1
                    );
                }

                upper.Add(
                    p
                );
            }

            lower.RemoveAt(
                lower.Count -
                1
            );

            upper.RemoveAt(
                upper.Count -
                1
            );

            lower.AddRange(
                upper
            );

            return lower;
        }

        private static float Cross(
            Vector2 a,
            Vector2 b,
            Vector2 c
        )
        {
            return
                (
                    b.x -
                    a.x
                ) *
                (
                    c.y -
                    a.y
                ) -
                (
                    b.y -
                    a.y
                ) *
                (
                    c.x -
                    a.x
                );
        }

        private static List<Vector2> SimplifyHull(
            List<Vector2> hull
        )
        {
            if (
                hull ==
                null ||
                hull.Count <=
                4
            )
            {
                return hull;
            }

            List<Vector2> result =
                new List<Vector2>();

            for (
                int i = 0;
                i < hull.Count;
                i++
            )
            {
                Vector2 previous =
                    hull[
                        (
                            i -
                            1 +
                            hull.Count
                        ) %
                        hull.Count
                    ];

                Vector2 current =
                    hull[i];

                Vector2 next =
                    hull[
                        (
                            i +
                            1
                        ) %
                        hull.Count
                    ];

                Vector2 a =
                    current -
                    previous;

                Vector2 b =
                    next -
                    current;

                if (
                    a.magnitude <
                    0.75f
                )
                {
                    continue;
                }

                if (
                    a.sqrMagnitude >
                    0.001f &&
                    b.sqrMagnitude >
                    0.001f
                )
                {
                    float dot =
                        Vector2.Dot(
                            a.normalized,
                            b.normalized
                        );

                    if (
                        dot >
                        0.985f
                    )
                    {
                        continue;
                    }
                }

                result.Add(
                    current
                );
            }

            if (
                result.Count <
                3
            )
            {
                return hull;
            }

            return result;
        }

        private List<Vector2> CreateScaffoldOutline(
            List<Vector2> footprint
        )
        {
            List<Vector2> result =
                new List<Vector2>();

            if (
                footprint ==
                null ||
                footprint.Count <
                3
            )
            {
                return result;
            }

            for (
                int i = 0;
                i < footprint.Count;
                i++
            )
            {
                Vector2 previous =
                    footprint[
                        (
                            i -
                            1 +
                            footprint.Count
                        ) %
                        footprint.Count
                    ];

                Vector2 current =
                    footprint[i];

                Vector2 next =
                    footprint[
                        (
                            i +
                            1
                        ) %
                        footprint.Count
                    ];

                Vector2 incoming =
                    (
                        current -
                        previous
                    ).normalized;

                Vector2 outgoing =
                    (
                        next -
                        current
                    ).normalized;

                Vector2 normalA =
                    new Vector2(
                        incoming.y,
                        -incoming.x
                    );

                Vector2 normalB =
                    new Vector2(
                        outgoing.y,
                        -outgoing.x
                    );

                Vector2 combined =
                    normalA +
                    normalB;

                if (
                    combined.sqrMagnitude <
                    0.001f
                )
                {
                    combined =
                        normalB;
                }

                combined.Normalize();

                float denominator =
                    Mathf.Max(
                        0.35f,
                        Vector2.Dot(
                            combined,
                            normalB
                        )
                    );

                float requiredClearance =
                    ScaffoldMargin +
                    ScaffoldGeometryClearance +
                    ScaffoldDeckDepth *
                    0.5f +
                    ScaffoldBeamThickness *
                    0.5f;

                float offset =
                    requiredClearance /
                    denominator;

                offset =
                    Mathf.Min(
                        offset,
                        requiredClearance *
                        2.8f
                    );

                result.Add(
                    current +
                    combined *
                    offset
                );
            }

            return result;
        }

        private GeometryAsset GetGeometryAsset(
            object renderPrefab
        )
        {
            if (
                renderPrefab ==
                null
            )
            {
                return null;
            }

            try
            {
                FieldInfo field =
                    renderPrefab
                        .GetType()
                        .GetField(
                            "m_GeometryAsset",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                if (
                    field ==
                    null
                )
                {
                    return null;
                }

                object reference =
                    field.GetValue(
                        renderPrefab
                    );

                return
                    ConvertAssetReference<GeometryAsset>(
                        reference
                    );
            }
            catch
            {
                return null;
            }
        }

        private static T ConvertAssetReference<T>(
            object assetReference
        )
            where T : class
        {
            if (
                assetReference ==
                null
            )
            {
                return null;
            }

            try
            {
                Type referenceType =
                    assetReference.GetType();

                MethodInfo[] methods =
                    referenceType.GetMethods(
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                foreach (
                    MethodInfo method
                    in methods
                )
                {
                    if (
                        method.Name !=
                        "op_Implicit" ||
                        !typeof(T).IsAssignableFrom(
                            method.ReturnType
                        )
                    )
                    {
                        continue;
                    }

                    ParameterInfo[] parameters =
                        method.GetParameters();

                    if (
                        parameters.Length !=
                        1
                    )
                    {
                        continue;
                    }

                    return
                        method.Invoke(
                            null,
                            new object[]
                            {
                                assetReference
                            }
                        ) as T;
                }
            }
            catch
            {
            }

            return null;
        }

        private void CreateNativeProxy(
            ConstructionVisual visual,
            PrefabRef prefabRef
        )
        {
            Entity source =
                visual.Source;

            Game.Objects.Transform transform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    source
                );

            EntityArchetype archetype =
                EntityManager.CreateArchetype(
                    typeof(PrefabRef),
                    typeof(Game.Objects.Transform),
                    typeof(Game.Objects.Object),
                    typeof(Game.Objects.ObjectGeometry),
                    typeof(Game.Objects.Static),
                    typeof(Created),
                    typeof(Updated),
                    typeof(Unity.Entities.Simulate)
                );

            Entity proxy =
                EntityManager.CreateEntity(
                    archetype
                );

            visual.Proxy =
                proxy;

            EntityManager.SetComponentData(
                proxy,
                prefabRef
            );

            EntityManager.SetComponentData(
                proxy,
                transform
            );

            CopyVisualComponent<CullingInfo>(
                source,
                proxy
            );

            CopyVisualComponent<Game.Objects.Color>(
                source,
                proxy
            );

            CopyVisualComponent<Game.Objects.Surface>(
                source,
                proxy
            );

            CopyVisualComponent<PseudoRandomSeed>(
                source,
                proxy
            );

            CopyMeshColorBuffer(
                source,
                proxy
            );

            CopyCustomMeshColorBuffer(
                source,
                proxy
            );

            if (
                !EntityManager.HasBuffer<MeshBatch>(
                    proxy
                )
            )
            {
                EntityManager.AddBuffer<MeshBatch>(
                    proxy
                );
            }
        }

        private void UpdateConstructionVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual ==
                null ||
                visual.Source ==
                Entity.Null ||
                visual.Proxy ==
                Entity.Null
            )
            {
                return;
            }

            if (
                !EntityManager.Exists(
                    visual.Source
                ) ||
                !EntityManager.Exists(
                    visual.Proxy
                )
            )
            {
                return;
            }

            if (
                !EntityManager.HasComponent<UnderConstruction>(
                    visual.Source
                )
            )
            {
                return;
            }

            if (
                visual.HiddenConstructionSandAreas.Count == 0 &&
                visual.ConstructionSandAreaScanAttempts < 5 &&
                UnityEngine.Time.unscaledTime >=
                visual.NextConstructionSandAreaScanTime
            )
            {
                HideOwnedConstructionSandAreas(
                    visual
                );
            }

            bool sourceHighlighted =
                EntityManager.HasComponent<Game.Tools.Highlighted>(
                    visual.Source
                );

            if (
                sourceHighlighted !=
                visual.WasSourceHighlighted
            )
            {
                visual.WasSourceHighlighted =
                    sourceHighlighted;

                visual.TerrainDirtRefreshPending =
                    true;
            }

            if (
                visual.TerrainDirtRefreshPending &&
                UnityEngine.Time.unscaledTime >=
                visual.NextTerrainDirtRefreshTime
            )
            {
                visual.TerrainDirtRefreshPending =
                    false;

                ApplyExperimentalTerrainDirt(
                    visual,
                    true
                );
            }

            Game.Objects.Transform sourceTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    visual.Source
                );

            UnderConstruction construction =
                EntityManager.GetComponentData<UnderConstruction>(
                    visual.Source
                );

            float targetProgress =
                math.saturate(
                    construction.m_Progress /
                    100f
                );

            float heightFactor =
                Mathf.Clamp(
                    visual.BuildingHeight /
                    20f,
                    0.35f,
                    2f
                );

            float effectiveSmoothTime =
                ProgressSmoothTime *
                heightFactor;

            float deltaTime =
                UnityEngine.Time.deltaTime;

            if (
                deltaTime >
                0f
            )
            {
                visual.VisualProgress =
                    Mathf.SmoothDamp(
                        visual.VisualProgress,
                        targetProgress,
                        ref visual.VisualProgressVelocity,
                        effectiveSmoothTime,
                        float.PositiveInfinity,
                        deltaTime
                    );

                visual.VisualProgress =
                    Mathf.Clamp01(
                        visual.VisualProgress
                    );
            }

            if (
                visual.BrandingEligible &&
                visual.CompanyBannerRoot == null &&
                UnityEngine.Time.unscaledTime >=
                visual.NextBrandingRetryTime
            )
            {
                CreateCompanyBanner(
                    visual
                );
            }

            UpdateBuildingProxy(
                visual,
                sourceTransform,
                visual.VisualProgress
            );

            UpdateScaffold(
                visual,
                sourceTransform,
                visual.VisualProgress
            );

            UpdateCranePosition(
                visual,
                sourceTransform
            );

            LogProgress(
                visual,
                construction
            );
        }

        private void UpdateBuildingProxy(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            float visualProgress
        )
        {
            if (
                visual.Proxy ==
                Entity.Null ||
                !EntityManager.Exists(
                    visual.Proxy
                )
            )
            {
                return;
            }

            float visibleProgress =
                Mathf.Clamp01(
                    visualProgress
                );

            float hiddenDistance =
                visual.BuildingHeight +
                0.5f;

            float verticalOffset =
                -hiddenDistance *
                (
                    1f -
                    visibleProgress
                );

            Game.Objects.Transform proxyTransform =
                sourceTransform;

            proxyTransform.m_Position.y +=
                verticalOffset;

            EntityManager.SetComponentData(
                visual.Proxy,
                proxyTransform
            );

            if (
                !EntityManager.HasComponent<Updated>(
                    visual.Proxy
                )
            )
            {
                EntityManager.AddComponent<Updated>(
                    visual.Proxy
                );
            }
        }

        private void UpdateScaffold(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            float visualProgress
        )
        {
            if (
                visual.ScaffoldRoot ==
                null ||
                visual.ScaffoldLevels.Count ==
                0
            )
            {
                return;
            }

            float3 position =
                sourceTransform.m_Position;

            visual.ScaffoldRoot
                .transform
                .position =
                new Vector3(
                    position.x,
                    position.y +
                    ScaffoldGroundOffset,
                    position.z
                );

            quaternion q =
                sourceTransform.m_Rotation;

            visual.ScaffoldRoot
                .transform
                .rotation =
                new Quaternion(
                    q.value.x,
                    q.value.y,
                    q.value.z,
                    q.value.w
                );

            int levelCount =
                visual.ScaffoldLevels.Count;

            float progress =
                Mathf.Clamp01(
                    visualProgress
                );

            if (
                progress <
                ScaffoldDismantleStart
            )
            {
                float buildingVisibleHeight =
                    visual.BuildingHeight *
                    progress;

                float scaffoldVisibleHeight =
                    Mathf.Min(
                        visual.ScaffoldHeight,
                        buildingVisibleHeight +
                        ScaffoldHeightLead
                    );

                UpdateCompanyBannerVisibility(
                    visual,
                    scaffoldVisibleHeight,
                    true
                );

                for (
                    int levelIndex = 0;
                    levelIndex < levelCount;
                    levelIndex++
                )
                {
                    GameObject level =
                        visual.ScaffoldLevels[
                            levelIndex
                        ];

                    if (
                        level ==
                        null
                    )
                    {
                        continue;
                    }

                    float floorBottom =
                        visual.ScaffoldLevelBottoms[
                            levelIndex
                        ];

                    float floorHeight =
                        visual.ScaffoldLevelHeights[
                            levelIndex
                        ];

                    float levelReveal =
                        Mathf.Clamp01(
                            (
                                scaffoldVisibleHeight -
                                floorBottom
                            ) /
                            Mathf.Max(
                                floorHeight,
                                0.01f
                            )
                        );

                    if (
                        levelReveal <=
                        0.001f
                    )
                    {
                        level.SetActive(
                            false
                        );

                        continue;
                    }

                    level.SetActive(
                        true
                    );

                    float easedReveal =
                        Smooth01(
                            levelReveal
                        );

                    level.transform.localScale =
                        new Vector3(
                            1f,
                            Mathf.Max(
                                easedReveal,
                                0.001f
                            ),
                            1f
                        );
                }
            }
            else
            {
                float dismantleProgress =
                    Mathf.Clamp01(
                        (
                            progress -
                            ScaffoldDismantleStart
                        ) /
                        (
                            1f -
                            ScaffoldDismantleStart
                        )
                    );

                dismantleProgress =
                    Smooth01(
                        dismantleProgress
                    );

                float dismantledLevelUnits =
                    dismantleProgress *
                    levelCount;

                float highestRemainingHeight =
                    0f;

                for (
                    int levelIndex = 0;
                    levelIndex < levelCount;
                    levelIndex++
                )
                {
                    GameObject level =
                        visual.ScaffoldLevels[
                            levelIndex
                        ];

                    if (
                        level == null
                    )
                    {
                        continue;
                    }

                    int topDownOrder =
                        levelCount -
                        1 -
                        levelIndex;

                    float levelDismantle =
                        Mathf.Clamp01(
                            dismantledLevelUnits -
                            topDownOrder
                        );

                    if (
                        levelDismantle >=
                        0.999f
                    )
                    {
                        level.SetActive(
                            false
                        );

                        continue;
                    }

                    level.SetActive(
                        true
                    );

                    float remainingScale =
                        1f -
                        Smooth01(
                            levelDismantle
                        );

                    level.transform.localScale =
                        new Vector3(
                            1f,
                            Mathf.Max(
                                remainingScale,
                                0.001f
                            ),
                            1f
                        );

                    float levelBottom =
                        visual.ScaffoldLevelBottoms[
                            levelIndex
                        ];

                    float levelHeight =
                        visual.ScaffoldLevelHeights[
                            levelIndex
                        ];

                    highestRemainingHeight =
                        Mathf.Max(
                            highestRemainingHeight,
                            levelBottom +
                            levelHeight *
                            remainingScale
                        );
                }

                UpdateCompanyBannerVisibility(
                    visual,
                    highestRemainingHeight,
                    highestRemainingHeight >
                    0.001f
                );
            }
        }

        private static float Smooth01(
            float value
        )
        {
            value =
                Mathf.Clamp01(
                    value
                );

            return
                value *
                value *
                (
                    3f -
                    2f *
                    value
                );
        }

        private void CreateScaffold(
            ConstructionVisual visual
        )
        {
            DestroyScaffold(
                visual
            );

            visual.ScaffoldRoot =
                new GameObject(
                    $"ConstructionAnimation_V1_42_5_Scaffold_" +
                    $"{visual.Source.Index}"
                );

            visual.ScaffoldRoot.hideFlags =
                HideFlags.DontSave;

            List<Vector2> outline =
                CreateScaffoldOutline(
                    visual.Footprint
                );

            if (
                outline.Count <
                3
            )
            {
                return;
            }

            if (
                visual.FloorBoundaries ==
                null ||
                visual.FloorBoundaries.Count <
                2
            )
            {
                visual.FloorBoundaries =
                    CreateFallbackFloors(
                        visual.BuildingHeight
                    );
            }

            visual.ScaffoldHeight =
                visual.FloorBoundaries[
                    visual.FloorBoundaries.Count -
                    1
                ];

            visual.ScaffoldLevels =
                new List<GameObject>();

            visual.ScaffoldLevelBottoms =
                new List<float>();

            visual.ScaffoldLevelHeights =
                new List<float>();

            int floorCount =
                visual.FloorBoundaries.Count -
                1;

            for (
                int levelIndex = 0;
                levelIndex < floorCount;
                levelIndex++
            )
            {
                float bottomY =
                    visual.FloorBoundaries[
                        levelIndex
                    ];

                float topY =
                    visual.FloorBoundaries[
                        levelIndex +
                        1
                    ];

                float levelHeight =
                    topY -
                    bottomY;

                if (
                    levelHeight <=
                    0.20f
                )
                {
                    continue;
                }

                GameObject levelRoot =
                    new GameObject(
                        $"ScaffoldFloor_" +
                        $"{levelIndex + 1}"
                    );

                levelRoot.hideFlags =
                    HideFlags.DontSave;

                levelRoot.transform.SetParent(
                    visual.ScaffoldRoot.transform,
                    false
                );

                levelRoot.transform.localPosition =
                    new Vector3(
                        0f,
                        bottomY,
                        0f
                    );

                levelRoot.transform.localRotation =
                    Quaternion.identity;

                levelRoot.transform.localScale =
                    Vector3.one;

                visual.ScaffoldLevels.Add(
                    levelRoot
                );

                visual.ScaffoldLevelBottoms.Add(
                    bottomY
                );

                visual.ScaffoldLevelHeights.Add(
                    levelHeight
                );

                CreateScaffoldLevel(
                    outline,
                    levelRoot,
                    levelHeight,
                    levelIndex == 0
                );

                levelRoot.SetActive(
                    false
                );

                ModLog.Info(
                    $"V1.42.5 scaffold floor " +
                    $"{levelIndex + 1}: " +
                    $"bottom={bottomY:0.00}m " +
                    $"height={levelHeight:0.00}m " +
                    $"top={topY:0.00}m"
                );
            }

            ModLog.Info(
                $"V1.42.5 scaffold created: " +
                $"source={visual.Source.Index}:" +
                $"{visual.Source.Version}, " +
                $"floors={visual.ScaffoldLevels.Count}, " +
                $"height={visual.ScaffoldHeight:0.00}m"
            );
        }

        private void CreateScaffoldLevel(
            List<Vector2> outline,
            GameObject levelRoot,
            float levelHeight,
            bool createBottomRing
        )
        {
            float bottomY =
                0f;

            float topY =
                levelHeight;

            for (
                int edgeIndex = 0;
                edgeIndex < outline.Count;
                edgeIndex++
            )
            {
                Vector2 a =
                    outline[
                        edgeIndex
                    ];

                Vector2 b =
                    outline[
                        (
                            edgeIndex +
                            1
                        ) %
                        outline.Count
                    ];

                Vector2 edge =
                    b -
                    a;

                float edgeLength =
                    edge.magnitude;

                if (
                    edgeLength <
                    0.04f
                )
                {
                    continue;
                }

                int bays =
                    Math.Max(
                        1,
                        Mathf.CeilToInt(
                            edgeLength /
                            ScaffoldBayWidth
                        )
                    );

                for (
                    int bay = 0;
                    bay < bays;
                    bay++
                )
                {
                    float t =
                        bay /
                        (float)bays;

                    Vector2 p =
                        Vector2.Lerp(
                            a,
                            b,
                            t
                        );

                    CreateBeamBetween(
                        levelRoot,
                        new Vector3(
                            p.x,
                            bottomY,
                            p.y
                        ),
                        new Vector3(
                            p.x,
                            topY,
                            p.y
                        ),
                        ScaffoldBeamThickness,
                        m_ScaffoldMetalMaterial
                    );
                }

                if (
                    createBottomRing
                )
                {
                    CreateBeamBetween(
                        levelRoot,
                        new Vector3(
                            a.x,
                            bottomY,
                            a.y
                        ),
                        new Vector3(
                            b.x,
                            bottomY,
                            b.y
                        ),
                        ScaffoldBeamThickness,
                        m_ScaffoldMetalMaterial
                    );
                }

                CreateBeamBetween(
                    levelRoot,
                    new Vector3(
                        a.x,
                        topY,
                        a.y
                    ),
                    new Vector3(
                        b.x,
                        topY,
                        b.y
                    ),
                    ScaffoldBeamThickness,
                    m_ScaffoldMetalMaterial
                );

                for (
                    int bay = 0;
                    bay < bays;
                    bay++
                )
                {
                    Vector2 p0 =
                        Vector2.Lerp(
                            a,
                            b,
                            bay /
                            (float)bays
                        );

                    Vector2 p1 =
                        Vector2.Lerp(
                            a,
                            b,
                            (
                                bay +
                                1
                            ) /
                            (float)bays
                        );

                    CreateBeamBetween(
                        levelRoot,
                        new Vector3(
                            p0.x,
                            bottomY,
                            p0.y
                        ),
                        new Vector3(
                            p1.x,
                            topY,
                            p1.y
                        ),
                        ScaffoldBeamThickness *
                        0.70f,
                        m_ScaffoldMetalMaterial
                    );

                    CreateBeamBetween(
                        levelRoot,
                        new Vector3(
                            p0.x,
                            topY,
                            p0.y
                        ),
                        new Vector3(
                            p1.x,
                            bottomY,
                            p1.y
                        ),
                        ScaffoldBeamThickness *
                        0.70f,
                        m_ScaffoldMetalMaterial
                    );
                }

                CreateDeckAlongEdge(
                    levelRoot,
                    a,
                    b,
                    topY +
                    0.05f
                );

            }

            CreateHorizontalFloorGrid(
                outline,
                levelRoot,
                topY
            );
        }

        private void CreatePeripheralFloorGridLegacy(
            List<Vector2> outline,
            GameObject parent,
            float y
        )
        {
            if (
                outline == null ||
                outline.Count < 3 ||
                parent == null
            )
            {
                return;
            }

            for (
                int edgeIndex = 0;
                edgeIndex < outline.Count;
                edgeIndex++
            )
            {
                Vector2 a =
                    outline[edgeIndex];

                Vector2 b =
                    outline[
                        (
                            edgeIndex +
                            1
                        ) %
                        outline.Count
                    ];

                Vector2 direction =
                    b -
                    a;

                float length =
                    direction.magnitude;

                if (
                    length < 0.04f
                )
                {
                    continue;
                }

                direction /=
                    length;

                Vector2 inward =
                    new Vector2(
                        -direction.y,
                        direction.x
                    );

                int crossBeamCount =
                    Math.Max(
                        1,
                        Mathf.CeilToInt(
                            length /
                            ScaffoldGridSpacing
                        )
                    );

                for (
                    int beamIndex = 0;
                    beamIndex <= crossBeamCount;
                    beamIndex++
                )
                {
                    float t =
                        beamIndex /
                        (
                            float
                        )crossBeamCount;

                    Vector2 outerPoint =
                        Vector2.Lerp(
                            a,
                            b,
                            t
                        );

                    Vector2 innerPoint =
                        outerPoint +
                        inward *
                        ScaffoldDeckDepth;

                    CreateBeamBetween(
                        parent,
                        new Vector3(
                            outerPoint.x,
                            y,
                            outerPoint.y
                        ),
                        new Vector3(
                            innerPoint.x,
                            y,
                            innerPoint.y
                        ),
                        ScaffoldGridBeamThickness,
                        m_ScaffoldMetalMaterial
                    );
                }
            }
        }

        private void UpdateCranePosition(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform
        )
        {
            if (
                visual == null ||
                visual.Footprint == null ||
                visual.Footprint.Count < 3
            )
            {
                return;
            }

            if (
                visual.CraneEntity == Entity.Null ||
                !EntityManager.Exists(
                    visual.CraneEntity
                )
            )
            {
                Entity foundCrane =
                    FindCraneSubObject(
                        visual.Source
                    );

                if (
                    foundCrane !=
                    visual.CraneEntity
                )
                {
                    visual.CraneEntity =
                        foundCrane;

                    visual.CraneVerticalOffsetCaptured =
                        false;

                    visual.CranePositionLogged =
                        false;
                }
            }

            if (
                visual.CraneEntity == Entity.Null ||
                !EntityManager.Exists(
                    visual.CraneEntity
                ) ||
                !EntityManager.HasComponent<Game.Objects.Transform>(
                    visual.CraneEntity
                )
            )
            {
                return;
            }

            Vector2 center =
                Vector2.zero;

            for (
                int i = 0;
                i < visual.Footprint.Count;
                i++
            )
            {
                center +=
                    visual.Footprint[i];
            }

            center /=
                visual.Footprint.Count;

            int cornerIndex =
                Math.Abs(
                    visual.Source.Index
                ) %
                visual.Footprint.Count;

            Vector2 corner =
                visual.Footprint[
                    cornerIndex
                ];

            Vector2 outward =
                corner -
                center;

            if (
                outward.sqrMagnitude < 0.001f
            )
            {
                outward =
                    Vector2.right;
            }

            outward.Normalize();

            Vector2 localCranePosition =
                corner +
                outward *
                (
                    ScaffoldMargin +
                    0.75f
                );

            float3 rotatedOffset =
                math.rotate(
                    sourceTransform.m_Rotation,
                    new float3(
                        localCranePosition.x,
                        0f,
                        localCranePosition.y
                    )
                );

            Game.Objects.Transform craneTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    visual.CraneEntity
                );

            if (
                !visual.CraneVerticalOffsetCaptured
            )
            {
                visual.CraneVerticalOffset =
                    craneTransform.m_Position.y -
                    sourceTransform.m_Position.y;

                visual.CraneVerticalOffsetCaptured =
                    true;
            }

            craneTransform.m_Position.x =
                sourceTransform.m_Position.x +
                rotatedOffset.x;

            craneTransform.m_Position.y =
                sourceTransform.m_Position.y +
                visual.CraneVerticalOffset;

            craneTransform.m_Position.z =
                sourceTransform.m_Position.z +
                rotatedOffset.z;

            EntityManager.SetComponentData(
                visual.CraneEntity,
                craneTransform
            );

            if (
                !EntityManager.HasComponent<Updated>(
                    visual.CraneEntity
                )
            )
            {
                EntityManager.AddComponent<Updated>(
                    visual.CraneEntity
                );
            }

            if (
                !visual.CranePositionLogged
            )
            {
                ModLog.Info(
                    $"V1.42.5 crane positioned " +
                    $"building={visual.Source.Index}:{visual.Source.Version}; " +
                    $"crane={visual.CraneEntity.Index}:{visual.CraneEntity.Version}; " +
                    $"corner={cornerIndex}; " +
                    $"local=({localCranePosition.x:0.00}," +
                    $"{localCranePosition.y:0.00}); " +
                    $"verticalOffset={visual.CraneVerticalOffset:0.00}"
                );

                visual.CranePositionLogged =
                    true;
            }
        }

        private Entity FindCraneSubObject(
            Entity owner
        )
        {
            if (
                owner == Entity.Null ||
                !EntityManager.Exists(
                    owner
                )
            )
            {
                return Entity.Null;
            }

            if (
                !EntityManager.HasBuffer<Game.Objects.SubObject>(
                    owner
                )
            )
            {
                return Entity.Null;
            }

            DynamicBuffer<Game.Objects.SubObject> subObjects =
                EntityManager.GetBuffer<Game.Objects.SubObject>(
                    owner
                );

            for (
                int i = 0;
                i < subObjects.Length;
                i++
            )
            {
                Entity subObject =
                    subObjects[i].m_SubObject;

                if (
                    subObject != Entity.Null &&
                    EntityManager.Exists(
                        subObject
                    ) &&
                    EntityManager.HasComponent<Game.Objects.Crane>(
                        subObject
                    )
                )
                {
                    return subObject;
                }
            }

            return Entity.Null;
        }

        private void CreateHorizontalFloorGrid(
            List<Vector2> outline,
            GameObject parent,
            float y
        )
        {
            if (
                outline ==
                null ||
                outline.Count <
                3 ||
                parent ==
                null
            )
            {
                return;
            }

            float minX =
                float.MaxValue;

            float maxX =
                float.MinValue;

            float minZ =
                float.MaxValue;

            float maxZ =
                float.MinValue;

            for (
                int i = 0;
                i < outline.Count;
                i++
            )
            {
                minX =
                    Mathf.Min(
                        minX,
                        outline[i].x
                    );

                maxX =
                    Mathf.Max(
                        maxX,
                        outline[i].x
                    );

                minZ =
                    Mathf.Min(
                        minZ,
                        outline[i].y
                    );

                maxZ =
                    Mathf.Max(
                        maxZ,
                        outline[i].y
                    );
            }

            float startX =
                Mathf.Ceil(
                    minX /
                    ScaffoldGridSpacing
                ) *
                ScaffoldGridSpacing;

            for (
                float x =
                    startX;
                x <=
                maxX +
                0.001f;
                x +=
                ScaffoldGridSpacing
            )
            {
                List<float> intersections =
                    GetVerticalPolygonIntersections(
                        outline,
                        x
                    );

                for (
                    int i = 0;
                    i + 1 < intersections.Count;
                    i += 2
                )
                {
                    float z0 =
                        intersections[i];

                    float z1 =
                        intersections[
                            i +
                            1
                        ];

                    if (
                        z1 -
                        z0 <
                        0.15f
                    )
                    {
                        continue;
                    }

                    CreateBeamBetween(
                        parent,
                        new Vector3(
                            x,
                            y,
                            z0
                        ),
                        new Vector3(
                            x,
                            y,
                            z1
                        ),
                        ScaffoldGridBeamThickness,
                        m_ScaffoldMetalMaterial
                    );
                }
            }

            float startZ =
                Mathf.Ceil(
                    minZ /
                    ScaffoldGridSpacing
                ) *
                ScaffoldGridSpacing;

            for (
                float z =
                    startZ;
                z <=
                maxZ +
                0.001f;
                z +=
                ScaffoldGridSpacing
            )
            {
                List<float> intersections =
                    GetHorizontalPolygonIntersections(
                        outline,
                        z
                    );

                for (
                    int i = 0;
                    i + 1 < intersections.Count;
                    i += 2
                )
                {
                    float x0 =
                        intersections[i];

                    float x1 =
                        intersections[
                            i +
                            1
                        ];

                    if (
                        x1 -
                        x0 <
                        0.15f
                    )
                    {
                        continue;
                    }

                    CreateBeamBetween(
                        parent,
                        new Vector3(
                            x0,
                            y,
                            z
                        ),
                        new Vector3(
                            x1,
                            y,
                            z
                        ),
                        ScaffoldGridBeamThickness,
                        m_ScaffoldMetalMaterial
                    );
                }
            }
        }

        private static List<float> GetVerticalPolygonIntersections(
            List<Vector2> polygon,
            float x
        )
        {
            List<float> result =
                new List<float>();

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 a =
                    polygon[i];

                Vector2 b =
                    polygon[
                        (
                            i +
                            1
                        ) %
                        polygon.Count
                    ];

                bool crosses =
                    (
                        a.x <=
                        x &&
                        b.x >
                        x
                    ) ||
                    (
                        b.x <=
                        x &&
                        a.x >
                        x
                    );

                if (
                    !crosses
                )
                {
                    continue;
                }

                float t =
                    (
                        x -
                        a.x
                    ) /
                    (
                        b.x -
                        a.x
                    );

                result.Add(
                    Mathf.Lerp(
                        a.y,
                        b.y,
                        t
                    )
                );
            }

            result.Sort();

            return result;
        }

        private static List<float> GetHorizontalPolygonIntersections(
            List<Vector2> polygon,
            float z
        )
        {
            List<float> result =
                new List<float>();

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 a =
                    polygon[i];

                Vector2 b =
                    polygon[
                        (
                            i +
                            1
                        ) %
                        polygon.Count
                    ];

                bool crosses =
                    (
                        a.y <=
                        z &&
                        b.y >
                        z
                    ) ||
                    (
                        b.y <=
                        z &&
                        a.y >
                        z
                    );

                if (
                    !crosses
                )
                {
                    continue;
                }

                float t =
                    (
                        z -
                        a.y
                    ) /
                    (
                        b.y -
                        a.y
                    );

                result.Add(
                    Mathf.Lerp(
                        a.x,
                        b.x,
                        t
                    )
                );
            }

            result.Sort();

            return result;
        }

        private void CreateDeckAlongEdge(
            GameObject parent,
            Vector2 a,
            Vector2 b,
            float y
        )
        {
            Vector2 direction2D =
                b -
                a;

            float length =
                direction2D.magnitude;

            if (
                length <
                0.04f
            )
            {
                return;
            }

            direction2D.Normalize();

            Vector2 lateral2D =
                new Vector2(
                    -direction2D.y,
                    direction2D.x
                );

            int segmentCount =
                1;

            int plankCount =
                Math.Max(
                    3,
                    Mathf.CeilToInt(
                        ScaffoldDeckDepth /
                        0.22f
                    )
                );

            float plankWidth =
                ScaffoldDeckDepth /
                plankCount;

            float plankVisualWidth =
                Mathf.Max(
                    0.08f,
                    plankWidth -
                    0.025f
                );

            for (
                int segmentIndex = 0;
                segmentIndex < segmentCount;
                segmentIndex++
            )
            {
                float t0 =
                    segmentIndex /
                    (float)segmentCount;

                float t1 =
                    (
                        segmentIndex +
                        1
                    ) /
                    (float)segmentCount;

                Vector2 segmentStart =
                    Vector2.Lerp(
                        a,
                        b,
                        t0
                    );

                Vector2 segmentEnd =
                    Vector2.Lerp(
                        a,
                        b,
                        t1
                    );

                Vector2 segmentCenter =
                    (
                        segmentStart +
                        segmentEnd
                    ) *
                    0.5f;

                float segmentLength =
                    Vector2.Distance(
                        segmentStart,
                        segmentEnd
                    );

                float visualLength =
                    Mathf.Max(
                        0.10f,
                        segmentLength +
                        0.08f
                    );

                for (
                    int plankIndex = 0;
                    plankIndex < plankCount;
                    plankIndex++
                )
                {
                    float lateralOffset =
                        -ScaffoldDeckDepth *
                        0.5f +
                        plankWidth *
                        (
                            plankIndex +
                            0.5f
                        );

                    Vector2 plankCenter =
                        segmentCenter +
                        lateral2D *
                        lateralOffset;

                    CreateOrientedBox(
                        parent,
                        new Vector3(
                            plankCenter.x,
                            y,
                            plankCenter.y
                        ),
                        new Vector3(
                            plankVisualWidth,
                            ScaffoldDeckThickness,
                            visualLength
                        ),
                        new Vector3(
                            direction2D.x,
                            0f,
                            direction2D.y
                        ),
                        m_ScaffoldDeckMaterial
                    );
                }
            }
        }

        private void CreateBeamBetween(
            GameObject parent,
            Vector3 start,
            Vector3 end,
            float thickness,
            Material material
        )
        {
            Vector3 direction =
                end -
                start;

            float length =
                direction.magnitude;

            if (
                length <
                0.001f
            )
            {
                return;
            }

            GameObject beam =
                GameObject.CreatePrimitive(
                    PrimitiveType.Cube
                );

            beam.name =
                "ScaffoldBeam";

            beam.hideFlags =
                HideFlags.DontSave;

            beam.transform.SetParent(
                parent.transform,
                false
            );

            beam.transform.localPosition =
                (
                    start +
                    end
                ) *
                0.5f;

            beam.transform.localRotation =
                Quaternion.FromToRotation(
                    Vector3.forward,
                    direction.normalized
                );

            beam.transform.localScale =
                new Vector3(
                    thickness,
                    thickness,
                    length
                );

            MeshRenderer renderer =
                beam.GetComponent<MeshRenderer>();

            if (
                renderer !=
                null &&
                material !=
                null
            )
            {
                renderer.sharedMaterial =
                    material;

                renderer.allowOcclusionWhenDynamic =
                    false;
            }

            RemovePrimitiveCollider(
                beam
            );
        }

        private void CreateOrientedBox(
            GameObject parent,
            Vector3 center,
            Vector3 size,
            Vector3 forward,
            Material material
        )
        {
            GameObject box =
                GameObject.CreatePrimitive(
                    PrimitiveType.Cube
                );

            box.name =
                "ScaffoldWoodDeck";

            box.hideFlags =
                HideFlags.DontSave;

            box.transform.SetParent(
                parent.transform,
                false
            );

            box.transform.localPosition =
                center;

            if (
                forward.sqrMagnitude >
                0.001f
            )
            {
                box.transform.localRotation =
                    Quaternion.FromToRotation(
                        Vector3.forward,
                        forward.normalized
                    );
            }
            else
            {
                box.transform.localRotation =
                    Quaternion.identity;
            }

            box.transform.localScale =
                size;

            MeshRenderer renderer =
                box.GetComponent<MeshRenderer>();

            if (
                renderer !=
                null &&
                material !=
                null
            )
            {
                renderer.sharedMaterial =
                    material;

                renderer.allowOcclusionWhenDynamic =
                    false;
            }

            RemovePrimitiveCollider(
                box
            );
        }

        private void CreateScaffoldTarpAlongEdge(
            GameObject parent,
            Vector2 start,
            Vector2 end,
            float bottomY,
            float topY
        )
        {
            if (
                !EnableSafetyTarp ||
                parent == null ||
                m_ScaffoldTarpMaterial == null ||
                m_ScaffoldTarpTexture == null
            )
            {
                return;
            }

            Vector2 edge =
                end -
                start;

            float edgeLength =
                edge.magnitude;

            float height =
                topY -
                bottomY;

            if (
                edgeLength < 0.04f ||
                height < 0.20f
            )
            {
                return;
            }

            Vector2 direction =
                edge /
                edgeLength;

            Vector3 outward =
                new Vector3(
                    direction.y,
                    0f,
                    -direction.x
                );

            Vector2 midpoint =
                (
                    start +
                    end
                ) *
                0.5f;

            GameObject tarp =
                GameObject.CreatePrimitive(
                    PrimitiveType.Quad
                );

            tarp.name =
                "ScaffoldSafetyTarp";

            tarp.hideFlags =
                HideFlags.DontSave;

            tarp.transform.SetParent(
                parent.transform,
                false
            );

            tarp.transform.localPosition =
                new Vector3(
                    midpoint.x,
                    (
                        bottomY +
                        topY
                    ) *
                    0.5f,
                    midpoint.y
                ) +
                outward *
                0.03f;

            tarp.transform.localRotation =
                Quaternion.LookRotation(
                    outward,
                    Vector3.up
                );

            MeshRenderer renderer =
                tarp.GetComponent<MeshRenderer>();

            tarp.transform.localScale =
                new Vector3(
                    edgeLength +
                    0.10f,
                    height,
                    1f
                );

            if (
                renderer != null
            )
            {
                renderer.sharedMaterial =
                    m_ScaffoldTarpMaterial;

                renderer.allowOcclusionWhenDynamic =
                    false;

                renderer.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;

                renderer.receiveShadows =
                    false;

                MaterialPropertyBlock properties =
                    new MaterialPropertyBlock();

                float textureScaleX =
                    Mathf.Max(
                        1f,
                        edgeLength /
                        2.20f
                    );

                float textureScaleY =
                    Mathf.Max(
                        1f,
                        height /
                        2.20f
                    );

                Vector4 textureTransform =
                    new Vector4(
                        textureScaleX,
                        textureScaleY,
                        0f,
                        0f
                    );

                properties.SetVector(
                    "_BaseColorMap_ST",
                    textureTransform
                );

                properties.SetVector(
                    "_BaseMap_ST",
                    textureTransform
                );

                renderer.SetPropertyBlock(
                    properties
                );
            }

            RemovePrimitiveCollider(
                tarp
            );
        }

        private static void RemovePrimitiveCollider(
            GameObject gameObject
        )
        {
            if (
                gameObject ==
                null
            )
            {
                return;
            }

            try
            {
                Component component =
                    gameObject.GetComponent(
                        "BoxCollider"
                    );

                if (
                    component !=
                    null
                )
                {
                    UnityEngine.Object.Destroy(
                        component
                    );
                }
            }
            catch
            {
            }
        }

        private void CreateScaffoldMaterials()
        {
            DestroyScaffoldMaterials();

            Shader shader =
                Shader.Find(
                    "HDRP/Lit"
                );

            if (
                shader ==
                null
            )
            {
                shader =
                    Shader.Find(
                        "Standard"
                    );
            }

            if (
                shader ==
                null
            )
            {
                ModLog.Info(
                    "V1.42.5 WARNING: no scaffold shader found."
                );

                return;
            }

            m_ScaffoldMetalBaseColorTexture =
                LoadEmbeddedTexture(
                    "ConstructionAnimation.Resources.Textures.ScaffoldMetal_BaseColor.jpg",
                    false
                );

            m_ScaffoldMetalMaskTexture =
                LoadEmbeddedTexture(
                    "ConstructionAnimation.Resources.Textures.ScaffoldMetal_MaskMap.png",
                    true
                );

            m_ScaffoldWoodBaseColorTexture =
                LoadEmbeddedTexture(
                    "ConstructionAnimation.Resources.Textures.ScaffoldWood_BaseColor.jpg",
                    false
                );

            m_ScaffoldWoodMaskTexture =
                LoadEmbeddedTexture(
                    "ConstructionAnimation.Resources.Textures.ScaffoldWood_MaskMap.png",
                    true
                );

            m_ScaffoldMetalMaterial =
                new Material(
                    shader
                );

            m_ScaffoldMetalMaterial.name =
                "ConstructionAnimation_ScaffoldMetal";

            ConfigureOpaqueDepthMaterial(
                m_ScaffoldMetalMaterial
            );

            SetMaterialColor(
                m_ScaffoldMetalMaterial,
                new UnityEngine.Color(
                    1f,
                    1f,
                    1f,
                    1f
                )
            );

            ApplyScaffoldTextures(
                m_ScaffoldMetalMaterial,
                m_ScaffoldMetalBaseColorTexture,
                m_ScaffoldMetalMaskTexture,
                new Vector2(
                    4f,
                    4f
                )
            );

            if (
                m_ScaffoldMetalMaterial.HasProperty(
                    "_Metallic"
                )
            )
            {
                m_ScaffoldMetalMaterial.SetFloat(
                    "_Metallic",
                    0.70f
                );
            }

            if (
                m_ScaffoldMetalMaterial.HasProperty(
                    "_Smoothness"
                )
            )
            {
                m_ScaffoldMetalMaterial.SetFloat(
                    "_Smoothness",
                    0.25f
                );
            }

            ValidateHdrpMaterial(
                m_ScaffoldMetalMaterial,
                "metal"
            );

            m_ScaffoldDeckMaterial =
                new Material(
                    shader
                );

            m_ScaffoldDeckMaterial.name =
                "ConstructionAnimation_ScaffoldWood";

            ConfigureOpaqueDepthMaterial(
                m_ScaffoldDeckMaterial
            );

            SetMaterialColor(
                m_ScaffoldDeckMaterial,
                new UnityEngine.Color(
                    1f,
                    1f,
                    1f,
                    1f
                )
            );

            ApplyScaffoldTextures(
                m_ScaffoldDeckMaterial,
                m_ScaffoldWoodBaseColorTexture,
                m_ScaffoldWoodMaskTexture,
                new Vector2(
                    2f,
                    2f
                )
            );

            if (
                m_ScaffoldDeckMaterial.HasProperty(
                    "_Metallic"
                )
            )
            {
                m_ScaffoldDeckMaterial.SetFloat(
                    "_Metallic",
                    0f
                );
            }

            if (
                m_ScaffoldDeckMaterial.HasProperty(
                    "_Smoothness"
                )
            )
            {
                m_ScaffoldDeckMaterial.SetFloat(
                    "_Smoothness",
                    0.08f
                );
            }

            ValidateHdrpMaterial(
                m_ScaffoldDeckMaterial,
                "wood"
            );

            m_ScaffoldTarpTexture =
                null;

            m_ScaffoldTarpMaterial =
                null;

            m_CompanyBannerMaterial =
                new Material(
                    shader
                );

            m_CompanyBannerMaterial.name =
                "ConstructionAnimation_CompanyBanner";

            ConfigureOpaqueDepthMaterial(
                m_CompanyBannerMaterial
            );

            SetMaterialColor(
                m_CompanyBannerMaterial,
                new UnityEngine.Color(
                    0.88f,
                    0.86f,
                    0.78f,
                    1f
                )
            );

            if (
                m_CompanyBannerMaterial.HasProperty(
                    "_Metallic"
                )
            )
            {
                m_CompanyBannerMaterial.SetFloat(
                    "_Metallic",
                    0f
                );
            }

            if (
                m_CompanyBannerMaterial.HasProperty(
                    "_Smoothness"
                )
            )
            {
                m_CompanyBannerMaterial.SetFloat(
                    "_Smoothness",
                    0.08f
                );
            }

            ValidateHdrpMaterial(
                m_CompanyBannerMaterial,
                "banner"
            );

        }

        private static void ConfigureSafetyMeshMaterial(
            Material material
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            material.SetOverrideTag(
                "RenderType",
                "Transparent"
            );

            material.renderQueue =
                3000;

            material.EnableKeyword(
                "_SURFACE_TYPE_TRANSPARENT"
            );

            material.DisableKeyword(
                "_ALPHATEST_ON"
            );

            if (
                material.HasProperty(
                    "_SurfaceType"
                )
            )
            {
                material.SetFloat(
                    "_SurfaceType",
                    1f
                );
            }

            if (
                material.HasProperty(
                    "_BlendMode"
                )
            )
            {
                material.SetFloat(
                    "_BlendMode",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_SrcBlend"
                )
            )
            {
                material.SetFloat(
                    "_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha
                );
            }

            if (
                material.HasProperty(
                    "_DstBlend"
                )
            )
            {
                material.SetFloat(
                    "_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha
                );
            }

            if (
                material.HasProperty(
                    "_ZWrite"
                )
            )
            {
                material.SetFloat(
                    "_ZWrite",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_TransparentZWrite"
                )
            )
            {
                material.SetFloat(
                    "_TransparentZWrite",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_AlphaCutoffEnable"
                )
            )
            {
                material.SetFloat(
                    "_AlphaCutoffEnable",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_CullMode"
                )
            )
            {
                material.SetFloat(
                    "_CullMode",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_TransparentCullMode"
                )
            )
            {
                material.SetFloat(
                    "_TransparentCullMode",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_DoubleSidedEnable"
                )
            )
            {
                material.SetFloat(
                    "_DoubleSidedEnable",
                    1f
                );
            }

            if (
                material.HasProperty(
                    "_EnableBlendModePreserveSpecularLighting"
                )
            )
            {
                material.SetFloat(
                    "_EnableBlendModePreserveSpecularLighting",
                    0f
                );
            }

        }

        private Texture2D LoadEmbeddedTexture(
            string resourceName,
            bool linear
        )
        {
            try
            {
                Assembly assembly =
                    Assembly.GetExecutingAssembly();

                using Stream stream =
                    assembly.GetManifestResourceStream(
                        resourceName
                    );

                if (
                    stream == null
                )
                {
                    ModLog.Info(
                        $"V1.42.5 texture resource missing: " +
                        $"{resourceName}"
                    );

                    return null;
                }

                byte[] bytes =
                    new byte[
                        stream.Length
                    ];

                int offset =
                    0;

                while (
                    offset < bytes.Length
                )
                {
                    int read =
                        stream.Read(
                            bytes,
                            offset,
                            bytes.Length -
                            offset
                        );

                    if (
                        read <= 0
                    )
                    {
                        break;
                    }

                    offset +=
                        read;
                }

                Texture2D texture =
                    new Texture2D(
                        2,
                        2,
                        TextureFormat.RGBA32,
                        true,
                        linear
                    );

                texture.name =
                    resourceName;

                texture.hideFlags =
                    HideFlags.DontSave;

                if (
                    !texture.LoadImage(
                        bytes,
                        false
                    )
                )
                {
                    UnityEngine.Object.Destroy(
                        texture
                    );

                    return null;
                }

                texture.wrapMode =
                    TextureWrapMode.Repeat;

                texture.filterMode =
                    FilterMode.Trilinear;

                texture.anisoLevel =
                    4;

                ModLog.Info(
                    $"V1.42.5 texture loaded: " +
                    $"{resourceName}; " +
                    $"{texture.width}x{texture.height}; " +
                    $"linear={linear}"
                );

                return texture;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 texture load failed: " +
                    $"{resourceName}; " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );

                return null;
            }
        }

        private static void ApplyScaffoldTextures(
            Material material,
            Texture2D baseColor,
            Texture2D maskMap,
            Vector2 tiling
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            if (
                baseColor != null
            )
            {
                if (
                    material.HasProperty(
                        "_BaseColorMap"
                    )
                )
                {
                    material.SetTexture(
                        "_BaseColorMap",
                        baseColor
                    );

                    material.SetTextureScale(
                        "_BaseColorMap",
                        tiling
                    );
                }

                if (
                    material.HasProperty(
                        "_MainTex"
                    )
                )
                {
                    material.SetTexture(
                        "_MainTex",
                        baseColor
                    );

                    material.SetTextureScale(
                        "_MainTex",
                        tiling
                    );
                }
            }

            if (
                maskMap != null
            )
            {
                if (
                    material.HasProperty(
                        "_MaskMap"
                    )
                )
                {
                    material.SetTexture(
                        "_MaskMap",
                        maskMap
                    );

                    material.SetTextureScale(
                        "_MaskMap",
                        tiling
                    );

                    material.EnableKeyword(
                        "_MASKMAP"
                    );
                }

                if (
                    material.HasProperty(
                        "_MetallicGlossMap"
                    )
                )
                {
                    material.SetTexture(
                        "_MetallicGlossMap",
                        maskMap
                    );

                    material.SetTextureScale(
                        "_MetallicGlossMap",
                        tiling
                    );

                    material.EnableKeyword(
                        "_METALLICGLOSSMAP"
                    );
                }
            }
        }

        private static void ConfigureOpaqueDepthMaterial(
            Material material
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            material.renderQueue =
                2000;

            material.DisableKeyword(
                "_SURFACE_TYPE_TRANSPARENT"
            );

            material.EnableKeyword(
                "_SURFACE_TYPE_OPAQUE"
            );

            if (
                material.HasProperty(
                    "_SurfaceType"
                )
            )
            {
                material.SetFloat(
                    "_SurfaceType",
                    0f
                );
            }

            if (
                material.HasProperty(
                    "_ZWrite"
                )
            )
            {
                material.SetFloat(
                    "_ZWrite",
                    1f
                );
            }

            if (
                material.HasProperty(
                    "_ZTest"
                )
            )
            {
                material.SetFloat(
                    "_ZTest",
                    4f
                );
            }

            if (
                material.HasProperty(
                    "_AlphaCutoffEnable"
                )
            )
            {
                material.SetFloat(
                    "_AlphaCutoffEnable",
                    0f
                );
            }

            material.SetOverrideTag(
                "RenderType",
                "Opaque"
            );
        }

        private static void ValidateHdrpMaterial(
            Material material,
            string materialRole
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            try
            {
                Type hdMaterialType =
                    Type.GetType(
                        "UnityEngine.Rendering.HighDefinition.HDMaterial, " +
                        "Unity.RenderPipelines.HighDefinition.Runtime"
                    );

                if (
                    hdMaterialType == null
                )
                {
                    ModLog.Info(
                        $"V1.42.5 HDRP validation unavailable " +
                        $"for {materialRole}."
                    );

                    return;
                }

                MethodInfo validateMethod =
                    hdMaterialType.GetMethod(
                        "ValidateMaterial",
                        BindingFlags.Static |
                        BindingFlags.Public,
                        null,
                        new Type[]
                        {
                            typeof(Material)
                        },
                        null
                    );

                if (
                    validateMethod == null
                )
                {
                    ModLog.Info(
                        $"V1.42.5 HDRP ValidateMaterial method " +
                        $"not found for {materialRole}."
                    );

                    return;
                }

                validateMethod.Invoke(
                    null,
                    new object[]
                    {
                        material
                    }
                );

                ModLog.Info(
                    $"V1.42.5 HDRP material validated: " +
                    $"{materialRole}; " +
                    $"shader={material.shader.name}; " +
                    $"queue={material.renderQueue}"
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    $"V1.42.5 HDRP material validation failed: " +
                    $"{materialRole}; " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );
            }
        }

        private static void SetMaterialColor(
            Material material,
            UnityEngine.Color color
        )
        {
            if (
                material ==
                null
            )
            {
                return;
            }

            if (
                material.HasProperty(
                    "_BaseColor"
                )
            )
            {
                material.SetColor(
                    "_BaseColor",
                    color
                );
            }

            if (
                material.HasProperty(
                    "_Color"
                )
            )
            {
                material.SetColor(
                    "_Color",
                    color
                );
            }

            if (
                material.HasProperty(
                    "_UnlitColor"
                )
            )
            {
                material.SetColor(
                    "_UnlitColor",
                    color
                );
            }

            try
            {
                material.color =
                    color;
            }
            catch
            {
            }
        }

        private void DestroyScaffoldMaterials()
        {
            if (
                m_ScaffoldMetalMaterial !=
                null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        m_ScaffoldMetalMaterial
                    );
                }
                catch
                {
                }

                m_ScaffoldMetalMaterial =
                    null;
            }

            if (
                m_ScaffoldDeckMaterial !=
                null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        m_ScaffoldDeckMaterial
                    );
                }
                catch
                {
                }

                m_ScaffoldDeckMaterial =
                    null;
            }

            if (
                m_ScaffoldTarpMaterial !=
                null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        m_ScaffoldTarpMaterial
                    );
                }
                catch
                {
                }

                m_ScaffoldTarpMaterial =
                    null;
            }

            if (
                m_CompanyBannerMaterial !=
                null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        m_CompanyBannerMaterial
                    );
                }
                catch
                {
                }

                m_CompanyBannerMaterial =
                    null;
            }

            DestroyTexture(
                ref m_ScaffoldMetalBaseColorTexture
            );

            DestroyTexture(
                ref m_ScaffoldMetalMaskTexture
            );

            DestroyTexture(
                ref m_ScaffoldWoodBaseColorTexture
            );

            DestroyTexture(
                ref m_ScaffoldWoodMaskTexture
            );

            DestroyTexture(
                ref m_ScaffoldTarpTexture
            );
        }

        private static void DestroyTexture(
            ref Texture2D texture
        )
        {
            if (
                texture == null
            )
            {
                return;
            }

            try
            {
                UnityEngine.Object.Destroy(
                    texture
                );
            }
            catch
            {
            }

            texture =
                null;
        }

        private void CopyVisualComponent<T>(
            Entity source,
            Entity target
        )
            where T : unmanaged,
            IComponentData
        {
            if (
                !EntityManager.HasComponent<T>(
                    source
                )
            )
            {
                return;
            }

            T value =
                EntityManager.GetComponentData<T>(
                    source
                );

            if (
                EntityManager.HasComponent<T>(
                    target
                )
            )
            {
                EntityManager.SetComponentData(
                    target,
                    value
                );
            }
            else
            {
                EntityManager.AddComponentData(
                    target,
                    value
                );
            }
        }

        private void CopyMeshColorBuffer(
            Entity source,
            Entity targetEntity
        )
        {
            if (
                !EntityManager.HasBuffer<MeshColor>(
                    targetEntity
                )
            )
            {
                EntityManager.AddBuffer<MeshColor>(
                    targetEntity
                );
            }

            DynamicBuffer<MeshColor> target =
                EntityManager.GetBuffer<MeshColor>(
                    targetEntity
                );

            target.Clear();

            if (
                !EntityManager.HasBuffer<MeshColor>(
                    source
                )
            )
            {
                return;
            }

            DynamicBuffer<MeshColor> sourceBuffer =
                EntityManager.GetBuffer<MeshColor>(
                    source
                );

            for (
                int i = 0;
                i < sourceBuffer.Length;
                i++
            )
            {
                target.Add(
                    sourceBuffer[i]
                );
            }
        }

        private void CopyCustomMeshColorBuffer(
            Entity source,
            Entity targetEntity
        )
        {
            if (
                !EntityManager.HasBuffer<CustomMeshColor>(
                    targetEntity
                )
            )
            {
                EntityManager.AddBuffer<CustomMeshColor>(
                    targetEntity
                );
            }

            DynamicBuffer<CustomMeshColor> target =
                EntityManager.GetBuffer<CustomMeshColor>(
                    targetEntity
                );

            target.Clear();

            if (
                !EntityManager.HasBuffer<CustomMeshColor>(
                    source
                )
            )
            {
                return;
            }

            DynamicBuffer<CustomMeshColor> sourceBuffer =
                EntityManager.GetBuffer<CustomMeshColor>(
                    source
                );

            for (
                int i = 0;
                i < sourceBuffer.Length;
                i++
            )
            {
                target.Add(
                    sourceBuffer[i]
                );
            }
        }

        private void SuppressVanillaSandSurfaces(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                )
            )
            {
                return;
            }

            HashSet<Entity> visited =
                new HashSet<Entity>();

            SuppressSourceConstructionSurface(
                visual
            );

            FindIndependentVanillaSandSurface(
                visual
            );

            FindAndSuppressVanillaSandSurfaces(
                visual,
                visual.Source,
                visited,
                0
            );

            float hiddenY =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    visual.Source
                ).m_Position.y -
                1000f;

            for (
                int i = 0;
                i < visual.HiddenVanillaSurfaces.Count;
                i++
            )
            {
                HiddenVanillaSurface hidden =
                    visual.HiddenVanillaSurfaces[i];

                if (
                    hidden.Entity == Entity.Null ||
                    !EntityManager.Exists(
                        hidden.Entity
                    )
                )
                {
                    continue;
                }

                if (
                    EntityManager.HasComponent<Game.Objects.Surface>(
                        hidden.Entity
                    )
                )
                {
                    EntityManager.RemoveComponent<Game.Objects.Surface>(
                        hidden.Entity
                    );
                }

                if (
                    EntityManager.HasComponent<Game.Objects.Transform>(
                        hidden.Entity
                    )
                )
                {
                    Game.Objects.Transform transform =
                        EntityManager.GetComponentData<Game.Objects.Transform>(
                            hidden.Entity
                        );

                    transform.m_Position.y =
                        hiddenY;

                    EntityManager.SetComponentData(
                        hidden.Entity,
                        transform
                    );
                }

                if (
                    !EntityManager.HasComponent<Updated>(
                        hidden.Entity
                    )
                )
                {
                    EntityManager.AddComponent<Updated>(
                        hidden.Entity
                    );
                }
            }
        }

        private void LogSurfaceComponentDetails(
            Entity source,
            Game.Objects.Surface surface
        )
        {
            if (
                m_SurfaceLayoutLogged
            )
            {
                return;
            }

            m_SurfaceLayoutLogged =
                true;

            try
            {
                Type surfaceType =
                    typeof(Game.Objects.Surface);

                ModLog.Info(
                    $"V1.42.16 Surface diagnostic begin: " +
                    $"source={source.Index}:{source.Version}; " +
                    $"type={surfaceType.FullName}"
                );

                object boxedSurface =
                    surface;

                FieldInfo[] fields =
                    surfaceType.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                ModLog.Info(
                    $"V1.42.16 Surface field count={fields.Length}"
                );

                for (
                    int i = 0;
                    i < fields.Length;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

                    object value =
                        null;

                    try
                    {
                        value =
                            field.GetValue(
                                boxedSurface
                            );
                    }
                    catch (Exception ex)
                    {
                        ModLog.Info(
                            $"V1.42.16 Surface field[{i}] " +
                            $"name={field.Name}; " +
                            $"type={field.FieldType.FullName}; " +
                            $"readError={ex.GetType().Name}"
                        );

                        continue;
                    }

                    string valueText =
                        value != null
                            ? value.ToString()
                            : "<null>";

                    string referencedPrefabName =
                        null;

                    if (
                        value is Entity
                    )
                    {
                        Entity referencedEntity =
                            (Entity)value;

                        valueText =
                            $"{referencedEntity.Index}:" +
                            $"{referencedEntity.Version}";

                        if (
                            referencedEntity != Entity.Null &&
                            EntityManager.Exists(
                                referencedEntity
                            )
                        )
                        {
                            try
                            {
                                referencedPrefabName =
                                    m_PrefabSystem.GetPrefabName(
                                        referencedEntity
                                    );
                            }
                            catch
                            {
                            }

                            if (
                                string.IsNullOrEmpty(
                                    referencedPrefabName
                                ) &&
                                EntityManager.HasComponent<PrefabRef>(
                                    referencedEntity
                                )
                            )
                            {
                                try
                                {
                                    PrefabRef referencedPrefab =
                                        EntityManager.GetComponentData<PrefabRef>(
                                            referencedEntity
                                        );

                                    referencedPrefabName =
                                        m_PrefabSystem.GetPrefabName(
                                            referencedPrefab.m_Prefab
                                        );
                                }
                                catch
                                {
                                }
                            }
                        }
                    }

                    ModLog.Info(
                        $"V1.42.16 Surface field[{i}] " +
                        $"name={field.Name}; " +
                        $"type={field.FieldType.FullName}; " +
                        $"value={valueText}; " +
                        $"prefab={referencedPrefabName ?? "<none>"}"
                    );
                }

                PropertyInfo[] properties =
                    surfaceType.GetProperties(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                ModLog.Info(
                    $"V1.42.16 Surface property count={properties.Length}"
                );

                for (
                    int i = 0;
                    i < properties.Length;
                    i++
                )
                {
                    PropertyInfo property =
                        properties[i];

                    ModLog.Info(
                        $"V1.42.16 Surface property[{i}] " +
                        $"name={property.Name}; " +
                        $"type={property.PropertyType.FullName}; " +
                        $"canRead={property.CanRead}; " +
                        $"canWrite={property.CanWrite}"
                    );
                }

                ModLog.Info(
                    "V1.42.16 Surface diagnostic end"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    $"V1.42.16 Surface diagnostic failed: {ex}"
                );
            }
        }

        private void SuppressSourceConstructionSurface(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                !EntityManager.HasComponent<Game.Objects.Surface>(
                    visual.Source
                )
            )
            {
                return;
            }

            try
            {
                if (
                    !visual.SourceSurfaceCaptured
                )
                {
                    visual.SourceSurface =
                        EntityManager.GetComponentData<Game.Objects.Surface>(
                            visual.Source
                        );

                    visual.SourceSurfaceCaptured =
                        true;

                    LogSurfaceComponentDetails(
                        visual.Source,
                        visual.SourceSurface
                    );

                    ModLog.Info(
                        $"V1.42.16 source construction surface captured: " +
                        $"{visual.Source.Index}:{visual.Source.Version}"
                    );
                }

                EntityManager.RemoveComponent<Game.Objects.Surface>(
                    visual.Source
                );

                if (
                    !EntityManager.HasComponent<Updated>(
                        visual.Source
                    )
                )
                {
                    EntityManager.AddComponent<Updated>(
                        visual.Source
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    $"V1.42.16 continuous source surface suppression failed: {ex}"
                );
            }
        }

        private void FindIndependentVanillaSandSurface(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                visual.HiddenVanillaSurfaces.Count > 0 ||
                m_SurfaceQuery.IsEmptyIgnoreFilter
            )
            {
                return;
            }

            Game.Objects.Transform sourceTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    visual.Source
                );

            float maximumDistance =
                Mathf.Max(
                    8f,
                    Mathf.Max(
                        visual.BuildingSize.x,
                        visual.BuildingSize.z
                    ) *
                    0.80f
                );

            float maximumDistanceSquared =
                maximumDistance *
                maximumDistance;

            Entity selected =
                Entity.Null;

            string selectedPrefabName =
                null;

            float selectedDistanceSquared =
                float.MaxValue;

            using NativeArray<Entity> surfaces =
                m_SurfaceQuery.ToEntityArray(
                    Allocator.Temp
                );

            for (
                int i = 0;
                i < surfaces.Length;
                i++
            )
            {
                Entity surfaceEntity =
                    surfaces[i];

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        surfaceEntity
                    );

                string prefabName =
                    null;

                try
                {
                    prefabName =
                        m_PrefabSystem.GetPrefabName(
                            prefabRef.m_Prefab
                        );
                }
                catch
                {
                }

                if (
                    string.IsNullOrEmpty(
                        prefabName
                    ) ||
                    prefabName.IndexOf(
                        "Sand Surface 02",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                Game.Objects.Transform surfaceTransform =
                    EntityManager.GetComponentData<Game.Objects.Transform>(
                        surfaceEntity
                    );

                float deltaX =
                    surfaceTransform.m_Position.x -
                    sourceTransform.m_Position.x;

                float deltaZ =
                    surfaceTransform.m_Position.z -
                    sourceTransform.m_Position.z;

                float distanceSquared =
                    deltaX *
                    deltaX +
                    deltaZ *
                    deltaZ;

                if (
                    distanceSquared >
                    maximumDistanceSquared ||
                    distanceSquared >=
                    selectedDistanceSquared
                )
                {
                    continue;
                }

                selected =
                    surfaceEntity;

                selectedPrefabName =
                    prefabName;

                selectedDistanceSquared =
                    distanceSquared;
            }

            if (
                selected == Entity.Null
            )
            {
                return;
            }

            HiddenVanillaSurface hidden =
                new HiddenVanillaSurface();

            hidden.Entity =
                selected;

            hidden.OriginalTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    selected
                );

            hidden.HadSurface =
                true;

            hidden.Surface =
                EntityManager.GetComponentData<Game.Objects.Surface>(
                    selected
                );

            visual.HiddenVanillaSurfaces.Add(
                hidden
            );

            ModLog.Info(
                $"V1.42.16 independent Sand Surface 02 matched: " +
                $"building={visual.Source.Index}:{visual.Source.Version}; " +
                $"entity={selected.Index}:{selected.Version}; " +
                $"prefab={selectedPrefabName}; " +
                $"distance={Mathf.Sqrt(selectedDistanceSquared):0.00}m"
            );
        }

        private void FindAndSuppressVanillaSandSurfaces(
            ConstructionVisual visual,
            Entity owner,
            HashSet<Entity> visited,
            int depth
        )
        {
            if (
                owner == Entity.Null ||
                !EntityManager.Exists(
                    owner
                ) ||
                depth > 4 ||
                !visited.Add(
                    owner
                ) ||
                !EntityManager.HasBuffer<Game.Objects.SubObject>(
                    owner
                )
            )
            {
                return;
            }

            DynamicBuffer<Game.Objects.SubObject> subObjects =
                EntityManager.GetBuffer<Game.Objects.SubObject>(
                    owner
                );

            for (
                int i = 0;
                i < subObjects.Length;
                i++
            )
            {
                Entity subObject =
                    subObjects[i].m_SubObject;

                if (
                    subObject == Entity.Null ||
                    !EntityManager.Exists(
                        subObject
                    )
                )
                {
                    continue;
                }

                string prefabName =
                    null;

                if (
                    EntityManager.HasComponent<PrefabRef>(
                        subObject
                    )
                )
                {
                    PrefabRef prefabRef =
                        EntityManager.GetComponentData<PrefabRef>(
                            subObject
                        );

                    try
                    {
                        prefabName =
                            m_PrefabSystem.GetPrefabName(
                                prefabRef.m_Prefab
                            );
                    }
                    catch
                    {
                    }
                }

                bool isSurface =
                    EntityManager.HasComponent<Game.Objects.Surface>(
                        subObject
                    );

                if (
                    (
                        isSurface ||
                        (
                            !string.IsNullOrEmpty(
                                prefabName
                            ) &&
                            prefabName.IndexOf(
                                "Surface",
                                StringComparison.OrdinalIgnoreCase
                            ) >= 0
                        )
                    ) &&
                    visual.LoggedSurfaceSubObjects.Add(
                        subObject
                    )
                )
                {
                    ModLog.Info(
                        $"V1.42.16 surface subobject discovered: " +
                        $"owner={owner.Index}:{owner.Version}; " +
                        $"entity={subObject.Index}:{subObject.Version}; " +
                        $"prefab={prefabName ?? "<none>"}; " +
                        $"hasSurface={isSurface}"
                    );
                }

                if (
                    !string.IsNullOrEmpty(
                        prefabName
                    ) &&
                    prefabName.IndexOf(
                        "Sand Surface 02",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0
                )
                {
                    bool alreadyHidden =
                        false;

                    for (
                        int hiddenIndex = 0;
                        hiddenIndex < visual.HiddenVanillaSurfaces.Count;
                        hiddenIndex++
                    )
                    {
                        if (
                            visual.HiddenVanillaSurfaces[hiddenIndex].Entity ==
                            subObject
                        )
                        {
                            alreadyHidden =
                                true;

                            break;
                        }
                    }

                    if (
                        !alreadyHidden &&
                        EntityManager.HasComponent<Game.Objects.Transform>(
                            subObject
                        )
                    )
                    {
                        HiddenVanillaSurface hidden =
                            new HiddenVanillaSurface();

                        hidden.Entity =
                            subObject;

                        hidden.OriginalTransform =
                            EntityManager.GetComponentData<Game.Objects.Transform>(
                                subObject
                            );

                        hidden.HadSurface =
                            isSurface;

                        if (
                            isSurface
                        )
                        {
                            hidden.Surface =
                                EntityManager.GetComponentData<Game.Objects.Surface>(
                                    subObject
                                );
                        }

                        visual.HiddenVanillaSurfaces.Add(
                            hidden
                        );

                        ModLog.Info(
                            $"V1.42.16 Sand Surface 02 hidden: " +
                            $"building={visual.Source.Index}:{visual.Source.Version}; " +
                            $"entity={subObject.Index}:{subObject.Version}; " +
                            $"prefab={prefabName}"
                        );
                    }
                }

                FindAndSuppressVanillaSandSurfaces(
                    visual,
                    subObject,
                    visited,
                    depth + 1
                );
            }
        }

        private void RestoreVanillaSandSurfaces(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            bool constructionStillActive =
                visual.Source != Entity.Null &&
                EntityManager.Exists(
                    visual.Source
                ) &&
                EntityManager.HasComponent<UnderConstruction>(
                    visual.Source
                );

            if (
                constructionStillActive &&
                visual.SourceSurfaceCaptured
            )
            {
                try
                {
                    if (
                        !EntityManager.HasComponent<Game.Objects.Surface>(
                            visual.Source
                        )
                    )
                    {
                        EntityManager.AddComponentData(
                            visual.Source,
                            visual.SourceSurface
                        );
                    }

                    if (
                        !EntityManager.HasComponent<Updated>(
                            visual.Source
                        )
                    )
                    {
                        EntityManager.AddComponent<Updated>(
                            visual.Source
                        );
                    }

                    ModLog.Info(
                        $"V1.42.16 source construction surface restored: " +
                        $"{visual.Source.Index}:{visual.Source.Version}"
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        $"V1.42.16 source surface restoration failed: {ex}"
                    );
                }
            }

            visual.SourceSurfaceCaptured =
                false;

            for (
                int i = 0;
                i < visual.HiddenVanillaSurfaces.Count;
                i++
            )
            {
                HiddenVanillaSurface hidden =
                    visual.HiddenVanillaSurfaces[i];

                if (
                    hidden.Entity == Entity.Null ||
                    !EntityManager.Exists(
                        hidden.Entity
                    )
                )
                {
                    ModLog.Info(
                        $"V1.42.16 Sand Surface 02 entity removed by game: " +
                        $"{hidden.Entity.Index}:{hidden.Entity.Version}"
                    );

                    continue;
                }

                if (
                    !constructionStillActive
                )
                {
                    ModLog.Info(
                        $"V1.42.16 Sand Surface 02 released after " +
                        $"construction end or bulldozer: " +
                        $"{hidden.Entity.Index}:{hidden.Entity.Version}"
                    );

                    continue;
                }

                try
                {
                    EntityManager.SetComponentData(
                        hidden.Entity,
                        hidden.OriginalTransform
                    );

                    if (
                        hidden.HadSurface &&
                        !EntityManager.HasComponent<Game.Objects.Surface>(
                            hidden.Entity
                        )
                    )
                    {
                        EntityManager.AddComponentData(
                            hidden.Entity,
                            hidden.Surface
                        );
                    }

                    if (
                        !EntityManager.HasComponent<Updated>(
                            hidden.Entity
                        )
                    )
                    {
                        EntityManager.AddComponent<Updated>(
                            hidden.Entity
                        );
                    }

                    ModLog.Info(
                        $"V1.42.16 Sand Surface 02 restored: " +
                        $"{hidden.Entity.Index}:{hidden.Entity.Version}"
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        $"V1.42.16 Sand Surface 02 restoration failed: {ex}"
                    );
                }
            }

            visual.HiddenVanillaSurfaces.Clear();

            visual.LoggedSurfaceSubObjects.Clear();
        }

        private void HideOwnedConstructionSandAreas(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                )
            )
            {
                return;
            }

            visual.ConstructionSandAreaScanAttempts++;

            visual.NextConstructionSandAreaScanTime =
                UnityEngine.Time.unscaledTime +
                1f;

            if (
                m_ConstructionSandAreaQuery.IsEmptyIgnoreFilter
            )
            {
                return;
            }

            using NativeArray<Entity> areas =
                m_ConstructionSandAreaQuery.ToEntityArray(
                    Allocator.Temp
                );

            for (
                int i = 0;
                i < areas.Length;
                i++
            )
            {
                Entity areaEntity =
                    areas[i];

                if (
                    areaEntity == Entity.Null ||
                    !EntityManager.Exists(
                        areaEntity
                    )
                )
                {
                    continue;
                }

                Owner owner =
                    EntityManager.GetComponentData<Owner>(
                        areaEntity
                    );

                if (
                    owner.m_Owner !=
                    visual.Source
                )
                {
                    continue;
                }

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        areaEntity
                    );

                string prefabName =
                    null;

                try
                {
                    prefabName =
                        m_PrefabSystem.GetPrefabName(
                            prefabRef.m_Prefab
                        );
                }
                catch
                {
                }

                if (
                    string.IsNullOrWhiteSpace(
                        prefabName
                    ) ||
                    prefabName.IndexOf(
                        "Sand Surface",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (
                    !EntityManager.HasComponent<Game.Areas.Surface>(
                        areaEntity
                    )
                )
                {
                    continue;
                }

                try
                {
                    SuppressedConstructionSandArea suppressed =
                        new SuppressedConstructionSandArea();

                    suppressed.Entity =
                        areaEntity;

                    suppressed.HadBatch =
                        EntityManager.HasComponent<Game.Areas.Batch>(
                            areaEntity
                        );

                    EntityManager.RemoveComponent<Game.Areas.Surface>(
                        areaEntity
                    );

                    if (
                        suppressed.HadBatch &&
                        EntityManager.HasComponent<Game.Areas.Batch>(
                            areaEntity
                        )
                    )
                    {
                        EntityManager.RemoveComponent<Game.Areas.Batch>(
                            areaEntity
                        );
                    }

                    if (
                        !EntityManager.HasComponent<Updated>(
                            areaEntity
                        )
                    )
                    {
                        EntityManager.AddComponent<Updated>(
                            areaEntity
                        );
                    }

                    visual.HiddenConstructionSandAreas.Add(
                        suppressed
                    );

                    ModLog.Info(
                        $"V1.42.18.3.1 owned construction Area.Surface and Area.Batch removed: " +
                        $"area={areaEntity.Index}:{areaEntity.Version}; " +
                        $"owner={visual.Source.Index}:{visual.Source.Version}; " +
                        $"prefab={prefabName}; " +
                        $"hadBatch={suppressed.HadBatch}"
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        $"V1.42.18.3.1 failed to remove owned construction area render components: {ex}"
                    );
                }
            }

            if (
                visual.HiddenConstructionSandAreas.Count == 0 &&
                visual.ConstructionSandAreaScanAttempts == 5
            )
            {
                ModLog.Info(
                    $"V1.42.18.3.1 no owned Sand Surface found after retries: " +
                    $"owner={visual.Source.Index}:{visual.Source.Version}"
                );
            }
        }

        private void RestoreOwnedConstructionSandAreas(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            bool constructionStillActive =
                visual.Source != Entity.Null &&
                EntityManager.Exists(
                    visual.Source
                ) &&
                EntityManager.HasComponent<UnderConstruction>(
                    visual.Source
                );

            if (
                !constructionStillActive
            )
            {
                visual.HiddenConstructionSandAreas.Clear();

                return;
            }

            for (
                int i = 0;
                i < visual.HiddenConstructionSandAreas.Count;
                i++
            )
            {
                SuppressedConstructionSandArea suppressed =
                    visual.HiddenConstructionSandAreas[i];

                Entity areaEntity =
                    suppressed.Entity;

                if (
                    areaEntity == Entity.Null ||
                    !EntityManager.Exists(
                        areaEntity
                    )
                )
                {
                    continue;
                }

                try
                {
                    if (
                        !EntityManager.HasComponent<Game.Areas.Surface>(
                            areaEntity
                        )
                    )
                    {
                        EntityManager.AddComponent<Game.Areas.Surface>(
                            areaEntity
                        );
                    }

                    if (
                        !EntityManager.HasComponent<Updated>(
                            areaEntity
                        )
                    )
                    {
                        EntityManager.AddComponent<Updated>(
                            areaEntity
                        );
                    }

                    ModLog.Info(
                        $"V1.42.18.3.1 owned construction Area.Surface restored; Area.Batch rebuild requested: " +
                        $"{areaEntity.Index}:{areaEntity.Version}"
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        $"V1.42.18.3.1 failed to restore owned construction area render components: {ex}"
                    );
                }
            }

            visual.HiddenConstructionSandAreas.Clear();
        }

        private void DestroyConstructionVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual ==
                null
            )
            {
                return;
            }

            RestoreOwnedConstructionSandAreas(
                visual
            );

            ClearExperimentalTerrainDirt(
                visual
            );

            RestoreVanillaSandSurfaces(
                visual
            );

            DestroyScaffold(
                visual
            );

            ScheduleNativeProxyDestroy(
                visual.Proxy
            );

            visual.Proxy =
                Entity.Null;

            visual.Source =
                Entity.Null;
        }

        private void DestroyScaffold(
            ConstructionVisual visual
        )
        {
            if (
                visual ==
                null
            )
            {
                return;
            }

            if (
                visual.ScaffoldRoot !=
                null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        visual.ScaffoldRoot
                    );
                }
                catch
                {
                }
            }

            visual.ScaffoldRoot =
                null;

            visual.CompanyBannerRoot =
                null;

            visual.CompanyEntity =
                Entity.Null;

            visual.CompanyPrefab =
                Entity.Null;

            visual.CompanyName =
                null;

            visual.CompanyBannerRequiredHeight =
                0f;

            visual.NextBrandingRetryTime =
                0f;

            visual.ScaffoldLevels.Clear();

            visual.ScaffoldLevelBottoms.Clear();

            visual.ScaffoldLevelHeights.Clear();

            visual.ScaffoldHeight =
                0f;
        }

        private void ScheduleNativeProxyDestroy(
            Entity proxy
        )
        {
            if (
                proxy ==
                Entity.Null ||
                !EntityManager.Exists(
                    proxy
                )
            )
            {
                return;
            }

            try
            {
                if (
                    !EntityManager.HasComponent<Deleted>(
                        proxy
                    )
                )
                {
                    EntityManager.AddComponent<Deleted>(
                        proxy
                    );
                }

                if (
                    !EntityManager.HasComponent<Updated>(
                        proxy
                    )
                )
                {
                    EntityManager.AddComponent<Updated>(
                        proxy
                    );
                }

                m_PendingProxyDestroys.Add(
                    new PendingProxyDestroy
                    {
                        Entity =
                            proxy,

                        Frames =
                            0
                    }
                );
            }
            catch
            {
                try
                {
                    if (
                        EntityManager.Exists(
                            proxy
                        )
                    )
                    {
                        EntityManager.DestroyEntity(
                            proxy
                        );
                    }
                }
                catch
                {
                }
            }
        }

        private void ProcessPendingProxyDestroys()
        {
            for (
                int i =
                    m_PendingProxyDestroys.Count -
                    1;
                i >=
                0;
                i--
            )
            {
                PendingProxyDestroy pending =
                    m_PendingProxyDestroys[
                        i
                    ];

                Entity proxy =
                    pending.Entity;

                if (
                    proxy ==
                    Entity.Null ||
                    !EntityManager.Exists(
                        proxy
                    )
                )
                {
                    m_PendingProxyDestroys.RemoveAt(
                        i
                    );

                    continue;
                }

                pending.Frames++;

                try
                {
                    if (
                        !EntityManager.HasComponent<Deleted>(
                            proxy
                        )
                    )
                    {
                        EntityManager.AddComponent<Deleted>(
                            proxy
                        );
                    }

                    if (
                        !EntityManager.HasComponent<Updated>(
                            proxy
                        )
                    )
                    {
                        EntityManager.AddComponent<Updated>(
                            proxy
                        );
                    }
                }
                catch
                {
                }

                if (
                    pending.Frames <
                    ProxyDestroyDelayFrames
                )
                {
                    continue;
                }

                try
                {
                    if (
                        EntityManager.Exists(
                            proxy
                        )
                    )
                    {
                        EntityManager.DestroyEntity(
                            proxy
                        );
                    }
                }
                catch
                {
                }

                m_PendingProxyDestroys.RemoveAt(
                    i
                );
            }
        }

        private void LogProgress(
            ConstructionVisual visual,
            UnderConstruction construction
        )
        {
            if (
                visual.LastProgress ==
                construction.m_Progress
            )
            {
                return;
            }

            byte progress =
                construction.m_Progress;

            if (
                progress ==
                0 ||
                progress ==
                10 ||
                progress ==
                25 ||
                progress ==
                50 ||
                progress ==
                60 ||
                progress ==
                75 ||
                progress ==
                90 ||
                progress >=
                98
            )
            {
                float buildingVisibleHeight =
                    visual.BuildingHeight *
                    visual.VisualProgress;

                ModLog.Info(
                    $"V1.42.5 source=" +
                    $"{visual.Source.Index}:" +
                    $"{visual.Source.Version} " +
                    $"realProgress={progress}% " +
                    $"visualProgress=" +
                    $"{visual.VisualProgress * 100f:0.0}% " +
                    $"visibleHeight=" +
                    $"{buildingVisibleHeight:0.00}m " +
                    $"floors=" +
                    $"{visual.ScaffoldLevels.Count}"
                );
            }

            visual.LastProgress =
                progress;
        }

        protected override void OnDestroy()
        {
            foreach (
                ConstructionVisual visual
                in m_Visuals.Values
            )
            {
                ClearExperimentalTerrainDirt(
                    visual
                );

                RestoreOwnedConstructionSandAreas(
                    visual
                );

                RestoreVanillaSandSurfaces(
                    visual
                );

                DestroyScaffold(
                    visual
                );

                if (
                    visual.Proxy !=
                    Entity.Null
                )
                {
                    try
                    {
                        if (
                            EntityManager.Exists(
                                visual.Proxy
                            )
                        )
                        {
                            EntityManager.DestroyEntity(
                                visual.Proxy
                            );
                        }
                    }
                    catch
                    {
                    }
                }
            }

            m_Visuals.Clear();

            for (
                int i = 0;
                i < m_PendingProxyDestroys.Count;
                i++
            )
            {
                Entity proxy =
                    m_PendingProxyDestroys[
                        i
                    ].Entity;

                try
                {
                    if (
                        proxy !=
                        Entity.Null &&
                        EntityManager.Exists(
                            proxy
                        )
                    )
                    {
                        EntityManager.DestroyEntity(
                            proxy
                        );
                    }
                }
                catch
                {
                }
            }

            m_PendingProxyDestroys.Clear();

            DestroyScaffoldMaterials();

            base.OnDestroy();
        }
    }
}
