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
using System.Reflection.Emit;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ConstructionAnimation.Systems
{
    public partial class ConstructionDetectionSystem : GameSystemBase
    {
        private const float CutoffHeightUpdateMinimumThreshold = 0.25f;
        private const float CutoffHeightUpdateMaximumThreshold = 0.60f;
        private const float CutoffTargetVerticalSteps = 240f;

        private EntityQuery m_BuildingQuery;
        private EntityQuery m_CompanyRenterQuery;
        private EntityQuery m_SurfaceQuery;
        private EntityQuery m_ConstructionSandAreaQuery;
        private PrefabSystem m_PrefabSystem;

        // V1.43.46.8: restored from the published build.
        // This is the game's own terrain-material painting system, not a
        // Unity GameObject overlay.
        private TerrainMaterialSystem m_TerrainMaterialSystem;
        private MethodInfo m_ApplyTerrainMaterialBrushMethod;
        private MethodInfo m_ForceUpdateWholeSplatmapMethod;
        private Entity m_DirtTerrainMaterialPrefab = Entity.Null;
        private Entity m_ClearTerrainMaterialPrefab = Entity.Null;
        private Entity m_RectangleBrushPrefab = Entity.Null;

        private bool m_TerrainSplatmapDirty;
        private float m_NextTerrainSplatmapFlushTime;

        private bool m_SurfaceLayoutLogged;

        private readonly HashSet<int> m_LoggedBuildingRenderPrefabs =
            new HashSet<int>();

        private int m_BuildingRenderProbeCount;

        private const int MaxBuildingRenderProbes = 12;

        private int m_LoadedSurfaceVTProbeCount;

        private const int MaxLoadedSurfaceVTProbes = 8;

        private bool m_ManagedBatchSystemProbed;

        private bool m_WindowInstancePropertyCatalogLogged;

        // V1.43.46.8: one-time reflection scan of the game's render pipeline.
        private bool m_UnderConstructionRenderPipelineProbed;

        private readonly HashSet<int> m_SourceRenderingProbeEntities =
            new HashSet<int>();

        private sealed class FootprintCandidate
        {
            public List<Vector2> Points =
                new List<Vector2>();

            public List<Vector3> PrincipalTriangleVertices =
                new List<Vector3>();

            public string PrefabName;

            public float Area;

            public float Compactness;

            public float Height;

            public float MinY;

            public float HeightCoverage;

            public float Score;
        }

        private struct CutoffVertexData
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector4 Tangent;
            public Vector4 UV0;
            public Vector4 UV1;
            public Vector4 UV2;
            public Vector4 UV3;
            public Vector4 UV4;
            public Vector4 UV5;
            public Vector4 UV6;
            public Vector4 UV7;
            public UnityEngine.Color Color;

            // >= 0 means this clipped vertex is exactly an original source vertex.
            // -1 means it was generated on the cutoff plane.
            public int SourceIndex;
        }

        private sealed class CutoffMeshVisual
        {
            public GameObject Root;
            public Mesh RuntimeMesh;

            public Vector3[] SourceVertices =
                new Vector3[0];

            public Vector3[] SourceNormals =
                new Vector3[0];

            public Vector4[] SourceTangents =
                new Vector4[0];

            // CS2 building shaders, especially VT/PVT and window shaders, can
            // consume UV channels beyond TEXCOORD0. Preserve all eight channels.
            public Vector4[][] SourceUVChannels =
                new Vector4[8][];

            public UnityEngine.Color[] SourceColors =
                new UnityEngine.Color[0];

            public int[][] SourceSubMeshTriangles =
                new int[0][];

            // Reusable CPU-side buffers. Original source vertices are mapped once
            // per rebuild and reused by index; only cutoff intersections add new
            // vertices. This avoids the old one-vertex-copy-per-triangle explosion.
            public readonly List<Vector3> RuntimeVertices =
                new List<Vector3>();

            public readonly List<Vector3> RuntimeNormals =
                new List<Vector3>();

            public readonly List<Vector4> RuntimeTangents =
                new List<Vector4>();

            public readonly List<Vector4>[] RuntimeUVChannels =
                new List<Vector4>[8]
                {
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>(),
                    new List<Vector4>()
                };

            public readonly List<UnityEngine.Color> RuntimeColors =
                new List<UnityEngine.Color>();

            public int[] SourceToRuntimeIndex =
                new int[0];

            public List<int>[] RuntimeSubMeshTriangles =
                new List<int>[0];

            public readonly List<CutoffVertexData> RuntimeClipped =
                new List<CutoffVertexData>(4);

            public readonly List<int> RuntimePolygonIndices =
                new List<int>(4);

            public bool HasNormals;
            public bool HasTangents;
            public readonly bool[] HasUVChannels =
                new bool[8];
            public bool HasColors;

            public float MinY;
            public float MaxY;

            public int SourceSubMeshIndex;
            public string SourcePrefabName;
        }

        private sealed class ScaffoldGeometryBuffer
        {
            public readonly List<Vector3> Vertices =
                new List<Vector3>();

            public readonly List<Vector3> Normals =
                new List<Vector3>();

            public readonly List<Vector2> UVs =
                new List<Vector2>();

            public readonly List<int> MetalTriangles =
                new List<int>();

            public readonly List<int> DeckTriangles =
                new List<int>();
        }

        private sealed class ConstructionVisual
        {
            public Entity Source = Entity.Null;
            public Entity Proxy = Entity.Null;

            public GameObject BuildingVisualRoot;
            public GameObject BuildingFoldRoot;
            public List<Mesh> BuildingVisualMeshes =
                new List<Mesh>();
            public List<Material> BuildingVisualMaterials =
                new List<Material>();
            public List<SurfaceAsset> BuildingLoadedSurfaceAssets =
                new List<SurfaceAsset>();
            public float BuildingVisualBaseY;


            public List<CutoffMeshVisual> CutoffMeshes =
                new List<CutoffMeshVisual>();

            public float CutoffLocalMinY;

            public float CutoffLocalMaxY;

            public float LastCutoffHeight;

            public bool HasCutoffHeight;

            public float LastCutoffLoggedProgress = -1f;

            // V1.43.46.8: safe native-render refresh experiment.
            // We never write MeshBatch ourselves. We only mark the real
            // under-construction entity Updated and observe whether the
            // game's own renderer populates its MeshBatch buffer.
            public bool NativeRenderRefreshRequested;

            public float NativeRenderRefreshRequestTime;

            public float NextNativeRenderRefreshProbeTime;

            public int NativeRenderRefreshProbeCount;

            public bool NativeRenderRefreshSucceeded;

            // V1.43.46.8: true only when THIS mod changed m_NewPrefab from Null
            // to the source entity's current PrefabRef. Vanilla transitions
            // that already have a non-null m_NewPrefab are left untouched.
            public bool NativeRenderGateInjected;

            public Entity NativeRenderGatePrefab = Entity.Null;

            public bool NativeRenderGateRegressionLogged;

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

            public float TerrainPaintWidth;

            public float TerrainPaintDepth;

            public float TerrainPaintAngle;

            public GameObject ConcreteStructureRoot;


            public GameObject RoofStructureRoot;

            public float RoofRevealStart =
                0.945f;

            public float RoofRevealEnd =
                0.995f;

            public List<ConcreteColumnVisual> ConcreteColumns =
                new List<ConcreteColumnVisual>();

            public List<ConcreteBeamLevelVisual> ConcreteBeamLevels =
                new List<ConcreteBeamLevelVisual>();

            public List<ConcreteSlabVisual> ConcreteSlabs =
                new List<ConcreteSlabVisual>();

            public List<ConcreteFloorFrameVisual> ConcreteFloorFrames =
                new List<ConcreteFloorFrameVisual>();

            public float BuildingHeight = 20f;

            public float3 BuildingSize =
                new float3(
                    20f,
                    20f,
                    20f
                );

            public float3 GeometryPivot =
                float3.zero;

            public bool HasLotDimensions;

            public float LotHalfWidth;

            public float LotHalfDepth;

            public List<Vector2> Footprint =
                new List<Vector2>();

            public List<List<Vector2>> FloorFootprints =
                new List<List<Vector2>>();

            public List<FloorRasterProfile> FloorRasterProfiles =
                new List<FloorRasterProfile>();

            public List<Vector3> StructureTriangleVertices =
                new List<Vector3>();

            public float StructureGeometryBaseY;

            public List<float> FloorBoundaries =
                new List<float>();

            public GameObject ScaffoldRoot;

            public List<GameObject> ScaffoldLevels =
                new List<GameObject>();

            public List<float> ScaffoldLevelBottoms =
                new List<float>();

            public List<float> ScaffoldLevelHeights =
                new List<float>();

            public List<Mesh> ScaffoldMeshes =
                new List<Mesh>();

            public List<MeshRenderer> ScaffoldRenderers =
                new List<MeshRenderer>();

            public int ScaffoldFullyRevealedCount;

            public int ScaffoldPartialRevealIndex = -1;

            public int ScaffoldFullyDismantledCount;

            public int ScaffoldPartialDismantleIndex = -1;

            public bool ScaffoldShadowsEnabled = true;

            public float ScaffoldHeight;

            public bool ScaffoldDistanceVisible =
                true;

            public float NextScaffoldDistanceCheckTime;

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

            public Entity CraneBackupEntity =
                Entity.Null;

            public bool CraneUsingBackup;

            public bool CraneEligibilityEvaluated;

            public bool CraneEligible = true;

            public string CraneSourcePrefabName;

            public float VisualProgress;

            public float VisualProgressVelocity;

            public byte LastProgress =
                255;

            public bool SeenThisFrame;

            public int MissingFrames;

            public bool Suspended;

            public bool CompletionHoldStarted;

            public float CompletionHoldStartTime;

            public bool CompletedAssetFadeStarted;

            public float CompletedAssetFadeStartTime;

            public bool Dismantling;

            public float DismantleStartTime;
        }

        private sealed class ConcreteColumnVisual
        {
            public GameObject Root = null;
            public Vector2 LocalPoint = Vector2.zero;
            public float BaseY = 0f;
            public float TargetHeight = 0f;
            public float Thickness = 0f;
            public float RevealStart = 0f;
            public float RevealEnd = 0f;
        }

        private sealed class ConcreteBeamLevelVisual
        {
            public GameObject Root = null;
            public float RevealAt = 0f;
        }

        private sealed class ConcreteSlabVisual
        {
            public GameObject Root = null;
            public float RevealAt = 0f;
        }

        private sealed class ConcreteFloorFrameVisual
        {
            public GameObject ColumnsRoot = null;
            public GameObject BeamsRoot = null;
            public float RevealStart = 0f;
            public float RevealEnd = 0f;
            public float BeamRevealAt = 0f;
        }

        private sealed class SliceSegment
        {
            public Vector2 A;
            public Vector2 B;
        }

        private sealed class RoofTriangleCandidate
        {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
            public Vector3 Normal;
            public float ProjectedArea;
            public float CentroidY;
            public float PlaneDistance;
        }

        private sealed class RoofPlaneGroup
        {
            public string Key;

            public Vector3 Normal;

            public float ProjectedArea;

            public float MaximumCentroidY;

            public List<RoofTriangleCandidate> Triangles =
                new List<RoofTriangleCandidate>();
        }

        private sealed class FloorRasterProfile
        {
            public float MinX;
            public float MinZ;
            public float CellSize;
            public int Width;
            public int Height;
            public bool[] OccupiedCells;
            public int OccupiedCount;

            public bool IsOccupied(
                int x,
                int z
            )
            {
                if (
                    x < 0 ||
                    z < 0 ||
                    x >= Width ||
                    z >= Height ||
                    OccupiedCells == null
                )
                {
                    return false;
                }

                return
                    OccupiedCells[
                        z *
                        Width +
                        x
                    ];
            }
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

            public string PrefabName;

            public bool HadSurface;

            public bool SurfaceRemoved;
        }

        private sealed class PendingProxyDestroy
        {
            public Entity Entity =
                Entity.Null;

            public int Frames;
        }

        private sealed class PendingUnityDestroy
        {
            public UnityEngine.Object Object;

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

        private readonly List<PendingUnityDestroy>
            m_PendingUnityDestroys =
                new List<PendingUnityDestroy>();

        private readonly Dictionary<Entity, int>
            m_CandidateSeenFrames =
                new Dictionary<Entity, int>();

        private readonly HashSet<Entity>
            m_QuerySourcesThisFrame =
                new HashSet<Entity>();

        private readonly List<Entity>
            m_RemoveCandidateSources =
                new List<Entity>();

        private readonly HashSet<string>
            m_LoggedBuildingShaderProfiles =
                new HashSet<string>();

        private Material m_ScaffoldMetalMaterial;
        private Material m_ScaffoldDeckMaterial;
        private Material m_CompanyBannerMaterial;
        private Material m_BuildingConstructionMaterial;
        private Texture2D m_ScaffoldMetalBaseColorTexture;
        private Texture2D m_ScaffoldMetalMaskTexture;
        private Texture2D m_ScaffoldWoodBaseColorTexture;
        private Texture2D m_ScaffoldWoodMaskTexture;
        private Camera m_RenderCamera;
        private float m_NextRenderCameraSearchTime;

        private float m_NextDiagnosticHeartbeatTime;
        private long m_DiagnosticUpdateSequence;
        private string m_LastDiagnosticStage =
            "not-started";
        private Entity m_LastDiagnosticSource =
            Entity.Null;

        private const int ProxyDestroyDelayFrames =
            4;

        private const int UnityDestroyDelayFrames =
            120;

        private const int VisualMissingGraceFrames =
            12;

        private const int MaxSimultaneousConstructionVisuals =
            64;

        private const int NewConstructionConfirmationFrames =
            1;

        private const float SuspendedProxyDepth =
            10000f;

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

        private const float CompletedAssetHoldDuration =
            1.5f;

        private const float CompletedAssetFadeDuration =
            1.5f;

        private const float ScaffoldDismantleDuration =
            10.0f;

        private const float FallbackTargetFloorHeight =
            2.75f;

        private const float WindowRowTolerance =
            0.35f;

        private const float ScaffoldGridSpacing =
            2.20f;

        private const float ScaffoldGridBeamThickness =
            0.07f;

        private const float CompanyBannerThickness =
            0.06f;

        private const float CompanyBannerMinWidth =
            2.20f;

        private const float CompanyBannerMaxWidth =
            6.50f;

        private const float ScaffoldMinimumCullDistance =
            500f;

        private const float ScaffoldMaximumCullDistance =
            900f;

        private const float ScaffoldCullDistancePerHeightMetre =
            2.20f;

        private const float ScaffoldCullHysteresis =
            50f;

        private const float SmallBuildingStableFootprintMaximumHeight =
            10f;

        private const float SmallBuildingStableFootprintMaximumArea =
            250f;

        private const float CraneLotEdgeInset =
            1.25f;

        private const float CraneLowResidentialMaximumHeight =
            12f;

        private const float CraneBackupParkDepth =
            5000f;

        private const float ScaffoldDistanceCheckInterval =
            0.35f;

        private const float ScaffoldShadowCullDistance =
            650f;

        private const float DiagnosticHeartbeatInterval =
            2.0f;

        private static readonly Vector3[] ScaffoldUnitCubeCorners =
            new Vector3[8]
            {
                new Vector3(-1f, -1f, -1f),
                new Vector3( 1f, -1f, -1f),
                new Vector3( 1f,  1f, -1f),
                new Vector3(-1f,  1f, -1f),
                new Vector3(-1f, -1f,  1f),
                new Vector3( 1f, -1f,  1f),
                new Vector3( 1f,  1f,  1f),
                new Vector3(-1f,  1f,  1f)
            };

        private static readonly int[] ScaffoldCubeFaceCorners =
            new int[24]
            {
                4, 5, 6, 7,
                1, 0, 3, 2,
                0, 4, 7, 3,
                5, 1, 2, 6,
                3, 7, 6, 2,
                0, 1, 5, 4
            };

        private static readonly Vector3[] ScaffoldCubeFaceNormals =
            new Vector3[6]
            {
                Vector3.forward,
                Vector3.back,
                Vector3.left,
                Vector3.right,
                Vector3.up,
                Vector3.down
            };

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

            ResolvePublishedTerrainPaintingApi();

            m_TerrainSplatmapDirty =
                false;

            m_NextTerrainSplatmapFlushTime =
                0f;

            ModLog.Info(
                "ConstructionAnimation V1.43.47.4.3.14: vertical cutoff with full UV-channel preservation, source MeshColor propagation, shared source-vertex reuse, original CS2 surface materials, and native ECS proxy still avoided."
            );

            ModLog.Checkpoint(
                "SYSTEM OnCreate complete; version=V1.43.47.4.3.14; " +
                "heartbeatInterval=" +
                DiagnosticHeartbeatInterval.ToString("0.0") +
                "s"
            );
        }

        private void ResolvePublishedTerrainPaintingApi()
        {
            m_ApplyTerrainMaterialBrushMethod =
                typeof(TerrainMaterialSystem).GetMethod(
                    "ApplyBrush",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            m_ForceUpdateWholeSplatmapMethod =
                typeof(TerrainMaterialSystem).GetMethod(
                    "ForceUpdateWholeSplatmap",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            m_DirtTerrainMaterialPrefab =
                Entity.Null;

            m_ClearTerrainMaterialPrefab =
                Entity.Null;

            m_RectangleBrushPrefab =
                Entity.Null;

            try
            {
                EntityQuery terrainMaterialQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<TerraformingData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> terrainMaterials =
                    terrainMaterialQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int i = 0;
                    i < terrainMaterials.Length;
                    i++
                )
                {
                    Entity prefabEntity =
                        terrainMaterials[i];

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

                EntityQuery brushQuery =
                    GetEntityQuery(
                        ComponentType.ReadOnly<BrushData>(),
                        ComponentType.ReadOnly<PrefabData>()
                    );

                using NativeArray<Entity> brushes =
                    brushQuery.ToEntityArray(
                        Allocator.Temp
                    );

                for (
                    int i = 0;
                    i < brushes.Length;
                    i++
                )
                {
                    Entity prefabEntity =
                        brushes[i];

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

                ModLog.Checkpoint(
                    "TERRAIN-PAINT published API resolved; " +
                    "applyMethod=" +
                    (m_ApplyTerrainMaterialBrushMethod != null) +
                    "; forceUpdateMethod=" +
                    (m_ForceUpdateWholeSplatmapMethod != null) +
                    "; dirt=" +
                    m_DirtTerrainMaterialPrefab.Index +
                    ":" +
                    m_DirtTerrainMaterialPrefab.Version +
                    "; clear=" +
                    m_ClearTerrainMaterialPrefab.Index +
                    ":" +
                    m_ClearTerrainMaterialPrefab.Version +
                    "; rectangleBrush=" +
                    m_RectangleBrushPrefab.Index +
                    ":" +
                    m_RectangleBrushPrefab.Version
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.37 published terrain painting API resolution failed: " +
                    ex
                );
            }
        }

        private bool TryReadAreaNodePosition(
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
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                object boxedNode =
                    node;

                for (
                    int i = 0;
                    i < fields.Length;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

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

        private bool TryGetOwnedConstructionAreaBoundsFromSubAreas(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            out float3 center,
            out float2 size
        )
        {
            center =
                sourceTransform.m_Position;

            size =
                float2.zero;

            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                !EntityManager.HasBuffer<Game.Areas.SubArea>(
                    visual.Source
                )
            )
            {
                return false;
            }

            quaternion inverseRotation =
                math.inverse(
                    sourceTransform.m_Rotation
                );

            bool found =
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

            DynamicBuffer<Game.Areas.SubArea> subAreas =
                EntityManager.GetBuffer<Game.Areas.SubArea>(
                    visual.Source
                );

            for (
                int i = 0;
                i < subAreas.Length;
                i++
            )
            {
                Entity areaEntity =
                    subAreas[i].m_Area;

                if (
                    areaEntity == Entity.Null ||
                    !EntityManager.Exists(
                        areaEntity
                    ) ||
                    !EntityManager.HasComponent<PrefabRef>(
                        areaEntity
                    ) ||
                    !EntityManager.HasBuffer<Game.Areas.Node>(
                        areaEntity
                    )
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
                    string.IsNullOrEmpty(
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
                    float3 worldPosition;

                    if (
                        !TryReadAreaNodePosition(
                            nodes[nodeIndex],
                            out worldPosition
                        )
                    )
                    {
                        continue;
                    }

                    float3 local =
                        math.rotate(
                            inverseRotation,
                            worldPosition -
                            sourceTransform.m_Position
                        );

                    float2 localXZ =
                        new float2(
                            local.x,
                            local.z
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

                    found =
                        true;
                }
            }

            if (
                !found
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

            float3 worldOffset =
                math.rotate(
                    sourceTransform.m_Rotation,
                    new float3(
                        localCenter.x,
                        0f,
                        localCenter.y
                    )
                );

            center =
                sourceTransform.m_Position +
                worldOffset;

            size =
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

        private void ApplyPublishedTerrainDirt(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.TerrainDirtPainted ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                m_TerrainMaterialSystem == null ||
                m_ApplyTerrainMaterialBrushMethod == null ||
                m_DirtTerrainMaterialPrefab == Entity.Null ||
                m_RectangleBrushPrefab == Entity.Null
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

                float3 paintPosition =
                    sourceTransform.m_Position;

                float width =
                    visual.HasLotDimensions
                        ? Mathf.Max(
                            0.5f,
                            visual.LotHalfWidth *
                            2f
                        )
                        : Mathf.Max(
                            0.5f,
                            visual.BuildingSize.x
                        );

                float depth =
                    visual.HasLotDimensions
                        ? Mathf.Max(
                            0.5f,
                            visual.LotHalfDepth *
                            2f
                        )
                        : Mathf.Max(
                            0.5f,
                            visual.BuildingSize.z
                        );

                float2 areaSize;

                if (
                    TryGetOwnedConstructionAreaBoundsFromSubAreas(
                        visual,
                        sourceTransform,
                        out paintPosition,
                        out areaSize
                    )
                )
                {
                    width =
                        areaSize.x;

                    depth =
                        areaSize.y;
                }
                else if (
                    visual.Footprint != null &&
                    visual.Footprint.Count >= 3
                )
                {
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

                    width =
                        Mathf.Max(
                            0.5f,
                            maximum.x -
                            minimum.x
                        );

                    depth =
                        Mathf.Max(
                            0.5f,
                            maximum.y -
                            minimum.y
                        );

                    Vector2 localCenter =
                        (
                            minimum +
                            maximum
                        ) *
                        0.5f;

                    float3 worldOffset =
                        math.rotate(
                            sourceTransform.m_Rotation,
                            new float3(
                                localCenter.x,
                                0f,
                                localCenter.y
                            )
                        );

                    paintPosition =
                        sourceTransform.m_Position +
                        worldOffset;
                }

                quaternion sourceRotation =
                    sourceTransform.m_Rotation;

                Quaternion unityRotation =
                    new Quaternion(
                        sourceRotation.value.x,
                        sourceRotation.value.y,
                        sourceRotation.value.z,
                        sourceRotation.value.w
                    );

                float angle =
                    unityRotation.eulerAngles.y *
                    Mathf.Deg2Rad;

                ApplyTerrainMaterialRectangle(
                    m_DirtTerrainMaterialPrefab,
                    paintPosition,
                    width,
                    depth,
                    angle
                );

                RequestTerrainSplatmapRefresh();

                visual.TerrainDirtPainted =
                    true;

                visual.TerrainPaintPosition =
                    paintPosition;

                visual.TerrainPaintWidth =
                    width;

                visual.TerrainPaintDepth =
                    depth;

                visual.TerrainPaintAngle =
                    angle;

                ModLog.Checkpoint(
                    "TERRAIN-PAINT dirt applied; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; width=" +
                    width.ToString("0.00") +
                    "; depth=" +
                    depth.ToString("0.00") +
                    "; angle=" +
                    angle.ToString("0.000")
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.37 published terrain dirt application failed: " +
                    ex
                );
            }
        }

        private void ClearPublishedTerrainDirt(
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

                RequestTerrainSplatmapRefresh();

                visual.TerrainDirtPainted =
                    false;

                ModLog.Checkpoint(
                    "TERRAIN-PAINT cleared"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.37 published terrain dirt clear failed: " +
                    ex
                );
            }
        }

        private void ApplyTerrainMaterialRectangle(
            Entity terrainMaterialPrefab,
            float3 position,
            float width,
            float depth,
            float angle
        )
        {
            width =
                Mathf.Max(
                    0.5f,
                    width
                );

            depth =
                Mathf.Max(
                    0.5f,
                    depth
                );

            bool widthIsLongAxis =
                width >=
                depth;

            float brushSize =
                Mathf.Min(
                    width,
                    depth
                );

            float longAxisSize =
                Mathf.Max(
                    width,
                    depth
                );

            int passCount =
                Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        longAxisSize /
                        brushSize
                    )
                );

            float travel =
                Mathf.Max(
                    0f,
                    longAxisSize -
                    brushSize
                );

            quaternion rotation =
                quaternion.RotateY(
                    angle
                );

            for (
                int i = 0;
                i < passCount;
                i++
            )
            {
                float t =
                    passCount == 1
                        ? 0.5f
                        : i /
                            (
                                float
                            )
                            (
                                passCount -
                                1
                            );

                float offset =
                    Mathf.Lerp(
                        -travel *
                        0.5f,
                        travel *
                        0.5f,
                        t
                    );

                float3 localOffset =
                    widthIsLongAxis
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

                float3 brushPosition =
                    position +
                    math.rotate(
                        rotation,
                        localOffset
                    );

                Game.Tools.Brush brush =
                    default;

                brush.m_Tool =
                    terrainMaterialPrefab;

                brush.m_Position =
                    brushPosition;

                brush.m_Target =
                    brushPosition;

                brush.m_Start =
                    brushPosition;

                brush.m_Angle =
                    angle;

                brush.m_Size =
                    brushSize;

                brush.m_Strength =
                    1f;

                brush.m_Opacity =
                    1f;

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

        private void RequestTerrainSplatmapRefresh()
        {
            m_TerrainSplatmapDirty =
                true;
        }

        private void FlushTerrainSplatmapRefreshIfNeeded()
        {
            if (
                !m_TerrainSplatmapDirty ||
                m_TerrainMaterialSystem == null ||
                m_ForceUpdateWholeSplatmapMethod == null
            )
            {
                return;
            }

            float now =
                global::UnityEngine.Time.realtimeSinceStartup;

            if (
                now <
                m_NextTerrainSplatmapFlushTime
            )
            {
                return;
            }

            try
            {
                m_ForceUpdateWholeSplatmapMethod.Invoke(
                    m_TerrainMaterialSystem,
                    null
                );

                m_TerrainSplatmapDirty =
                    false;

                m_NextTerrainSplatmapFlushTime =
                    now +
                    0.5f;

                ModLog.Checkpoint(
                    "TERRAIN-PAINT splatmap flush"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.37 batched terrain splatmap refresh failed: " +
                    ex
                );
            }
        }

        private void ProbeUnderConstructionRenderPipelineOnce()
        {
            if (
                m_UnderConstructionRenderPipelineProbed
            )
            {
                return;
            }

            m_UnderConstructionRenderPipelineProbed =
                true;

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

                int displayPropertyMatches =
                    0;

                int underConstructionMemberMatches =
                    0;

                int batchMethodMatches =
                    0;

                ModLog.Checkpoint(
                    "UNDER-CONSTRUCTION-RENDER-PROBE begin; assembly=" +
                    gameAssembly.GetName().Name +
                    "; types=" +
                    (
                        types != null
                            ? types.Length
                            : 0
                    )
                );

                if (
                    types == null
                )
                {
                    return;
                }

                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
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
                            property == null
                        )
                        {
                            continue;
                        }

                        if (
                            string.Equals(
                                property.Name,
                                "displayForUnderConstruction",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            displayPropertyMatches++;

                            MethodInfo getter =
                                null;

                            try
                            {
                                getter =
                                    property.GetGetMethod(
                                        true
                                    );
                            }
                            catch
                            {
                            }

                            ModLog.Checkpoint(
                                "UNDER-CONSTRUCTION-RENDER-PROBE display-property; " +
                                "declaringType=" +
                                type.FullName +
                                "; propertyType=" +
                                (
                                    property.PropertyType != null
                                        ? property.PropertyType.FullName
                                        : "null"
                                ) +
                                "; canRead=" +
                                property.CanRead +
                                "; canWrite=" +
                                property.CanWrite +
                                "; getterStatic=" +
                                (
                                    getter != null &&
                                    getter.IsStatic
                                )
                            );
                        }

                        if (
                            property.Name.IndexOf(
                                "UnderConstruction",
                                StringComparison.OrdinalIgnoreCase
                            ) >= 0 &&
                            !string.Equals(
                                property.Name,
                                "displayForUnderConstruction",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            underConstructionMemberMatches++;

                            ModLog.Checkpoint(
                                "UNDER-CONSTRUCTION-RENDER-PROBE property; " +
                                "declaringType=" +
                                type.FullName +
                                "; name=" +
                                property.Name +
                                "; propertyType=" +
                                (
                                    property.PropertyType != null
                                        ? property.PropertyType.FullName
                                        : "null"
                                )
                            );
                        }
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
                                "UnderConstruction",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                        {
                            continue;
                        }

                        underConstructionMemberMatches++;

                        ModLog.Checkpoint(
                            "UNDER-CONSTRUCTION-RENDER-PROBE field; " +
                            "declaringType=" +
                            type.FullName +
                            "; name=" +
                            field.Name +
                            "; fieldType=" +
                            (
                                field.FieldType != null
                                    ? field.FieldType.FullName
                                    : "null"
                            ) +
                            "; static=" +
                            field.IsStatic
                        );
                    }

                    MethodInfo[] methods;

                    try
                    {
                        methods =
                            type.GetMethods(
                                flags
                            );
                    }
                    catch
                    {
                        methods =
                            Array.Empty<MethodInfo>();
                    }

                    for (
                        int methodIndex = 0;
                        methodIndex < methods.Length;
                        methodIndex++
                    )
                    {
                        MethodInfo methodInfo =
                            methods[methodIndex];

                        if (
                            methodInfo == null
                        )
                        {
                            continue;
                        }

                        bool interestingBatchMethod =
                            string.Equals(
                                methodInfo.Name,
                                "UpdateObjectBatches",
                                StringComparison.OrdinalIgnoreCase
                            ) ||
                            string.Equals(
                                methodInfo.Name,
                                "UpdateSubObjectBatches",
                                StringComparison.OrdinalIgnoreCase
                            ) ||
                            string.Equals(
                                methodInfo.Name,
                                "UpdateBatches",
                                StringComparison.OrdinalIgnoreCase
                            ) ||
                            string.Equals(
                                methodInfo.Name,
                                "ResetMeshBatches",
                                StringComparison.OrdinalIgnoreCase
                            ) ||
                            string.Equals(
                                methodInfo.Name,
                                "GenerateSubBatches",
                                StringComparison.OrdinalIgnoreCase
                            ) ||
                            string.Equals(
                                methodInfo.Name,
                                "GetManagedBatches",
                                StringComparison.OrdinalIgnoreCase
                            );

                        if (
                            !interestingBatchMethod
                        )
                        {
                            continue;
                        }

                        batchMethodMatches++;

                        ParameterInfo[] parameters =
                            methodInfo.GetParameters();

                        List<string> parameterDescriptions =
                            new List<string>();

                        for (
                            int parameterIndex = 0;
                            parameterIndex < parameters.Length;
                            parameterIndex++
                        )
                        {
                            ParameterInfo parameter =
                                parameters[parameterIndex];

                            parameterDescriptions.Add(
                                (
                                    parameter.ParameterType != null
                                        ? parameter.ParameterType.FullName
                                        : "null"
                                ) +
                                " " +
                                parameter.Name
                            );
                        }

                        ModLog.Checkpoint(
                            "UNDER-CONSTRUCTION-RENDER-PROBE batch-method; " +
                            "declaringType=" +
                            type.FullName +
                            "; name=" +
                            methodInfo.Name +
                            "; return=" +
                            (
                                methodInfo.ReturnType != null
                                    ? methodInfo.ReturnType.FullName
                                    : "null"
                            ) +
                            "; static=" +
                            methodInfo.IsStatic +
                            "; params=" +
                            string.Join(
                                ",",
                                parameterDescriptions
                            )
                        );
                    }
                }

                ProbeNativeRenderingIl(
                    gameAssembly
                );

                ModLog.Checkpoint(
                    "UNDER-CONSTRUCTION-RENDER-PROBE end; " +
                    "displayPropertyMatches=" +
                    displayPropertyMatches +
                    "; underConstructionMembers=" +
                    underConstructionMemberMatches +
                    "; batchMethods=" +
                    batchMethodMatches
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 under-construction render pipeline probe failed: " +
                    ex
                );
            }
        }

        private sealed class DecodedIlInstruction
        {
            public int Offset;

            public string Text =
                string.Empty;

            public string ResolvedOperand =
                string.Empty;
        }

        private void ProbeNativeRenderingIl(
            Assembly gameAssembly
        )
        {
            try
            {
                ModLog.Checkpoint(
                    "NATIVE-IL-PROBE begin"
                );

                ProbeNativeRenderingType(
                    gameAssembly,
                    "Game.Rendering.RequiredBatchesSystem+RequiredBatchesJob",
                    new[]
                    {
                        "UpdateObjectBatches"
                    }
                );

                ProbeNativeRenderingType(
                    gameAssembly,
                    "Game.Rendering.BatchInstanceSystem+BatchInstanceJob",
                    new[]
                    {
                        "Execute"
                    }
                );

                ProbeNativeRenderingType(
                    gameAssembly,
                    "Game.Rendering.ObjectColorSystem+UpdateObjectColorsJob",
                    new[]
                    {
                        "Execute"
                    }
                );

                ModLog.Checkpoint(
                    "NATIVE-IL-PROBE end"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 native IL probe failed: " +
                    ex
                );
            }
        }

        private void ProbeNativeRenderingType(
            Assembly gameAssembly,
            string fullTypeName,
            string[] targetMethodNames
        )
        {
            Type type =
                gameAssembly.GetType(
                    fullTypeName,
                    false
                );

            if (
                type == null
            )
            {
                ModLog.Checkpoint(
                    "NATIVE-IL-PROBE type-not-found; type=" +
                    fullTypeName
                );

                return;
            }

            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            try
            {
                FieldInfo[] fields =
                    type.GetFields(
                        flags
                    );

                for (
                    int i = 0;
                    i < fields.Length;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

                    ModLog.Checkpoint(
                        "NATIVE-IL-PROBE target-field; type=" +
                        fullTypeName +
                        "; name=" +
                        field.Name +
                        "; fieldType=" +
                        (
                            field.FieldType != null
                                ? field.FieldType.FullName
                                : "null"
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 target-field probe failed; type=" +
                    fullTypeName +
                    "; exception=" +
                    ex
                );
            }

            MethodInfo[] methods =
                type.GetMethods(
                    flags
                );

            for (
                int i = 0;
                i < methods.Length;
                i++
            )
            {
                MethodInfo method =
                    methods[i];

                bool selected =
                    false;

                for (
                    int j = 0;
                    j < targetMethodNames.Length;
                    j++
                )
                {
                    if (
                        string.Equals(
                            method.Name,
                            targetMethodNames[j],
                            StringComparison.Ordinal
                        )
                    )
                    {
                        selected =
                            true;

                        break;
                    }
                }

                if (
                    !selected
                )
                {
                    continue;
                }

                DumpInterestingMethodIl(
                    method
                );
            }
        }

        private void DumpInterestingMethodIl(
            MethodInfo method
        )
        {
            MethodBody body;

            try
            {
                body =
                    method.GetMethodBody();
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 IL body unavailable; method=" +
                    method.DeclaringType?.FullName +
                    "::" +
                    method.Name +
                    "; exception=" +
                    ex
                );

                return;
            }

            if (
                body == null
            )
            {
                ModLog.Checkpoint(
                    "NATIVE-IL-PROBE no-body; method=" +
                    method.DeclaringType?.FullName +
                    "::" +
                    method.Name
                );

                return;
            }

            byte[] il =
                body.GetILAsByteArray();

            if (
                il == null ||
                il.Length == 0
            )
            {
                ModLog.Checkpoint(
                    "NATIVE-IL-PROBE empty-body; method=" +
                    method.DeclaringType?.FullName +
                    "::" +
                    method.Name
                );

                return;
            }

            Dictionary<short, OpCode> opCodes =
                new Dictionary<short, OpCode>();

            FieldInfo[] opcodeFields =
                typeof(OpCodes).GetFields(
                    BindingFlags.Public |
                    BindingFlags.Static
                );

            for (
                int i = 0;
                i < opcodeFields.Length;
                i++
            )
            {
                object value =
                    opcodeFields[i].GetValue(
                        null
                    );

                if (
                    value is OpCode opCode
                )
                {
                    opCodes[opCode.Value] =
                        opCode;
                }
            }

            List<DecodedIlInstruction> decoded =
                new List<DecodedIlInstruction>();

            int position =
                0;

            Module module =
                method.Module;

            Type[] typeArguments =
                method.DeclaringType != null &&
                method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments()
                    : Type.EmptyTypes;

            Type[] methodArguments =
                method.IsGenericMethod
                    ? method.GetGenericArguments()
                    : Type.EmptyTypes;

            while (
                position < il.Length
            )
            {
                int offset =
                    position;

                short opcodeValue;

                byte first =
                    il[position++];

                if (
                    first == 0xFE
                )
                {
                    if (
                        position >= il.Length
                    )
                    {
                        break;
                    }

                    opcodeValue =
                        unchecked(
                            (short)(
                                0xFE00 |
                                il[position++]
                            )
                        );
                }
                else
                {
                    opcodeValue =
                        first;
                }

                if (
                    !opCodes.TryGetValue(
                        opcodeValue,
                        out OpCode opCode
                    )
                )
                {
                    decoded.Add(
                        new DecodedIlInstruction
                        {
                            Offset = offset,
                            Text =
                                "IL_" +
                                offset.ToString(
                                    "X4"
                                ) +
                                ": <unknown opcode 0x" +
                                (
                                    (ushort)opcodeValue
                                ).ToString(
                                    "X4"
                                ) +
                                ">"
                        }
                    );

                    break;
                }

                string operandText =
                    string.Empty;

                string resolvedOperand =
                    string.Empty;

                try
                {
                    switch (
                        opCode.OperandType
                    )
                    {
                        case OperandType.InlineNone:
                            break;

                        case OperandType.ShortInlineI:
                            operandText =
                                ((sbyte)il[position]).ToString();

                            position +=
                                1;

                            break;

                        case OperandType.InlineI:
                            operandText =
                                BitConverter.ToInt32(
                                    il,
                                    position
                                ).ToString();

                            position +=
                                4;

                            break;

                        case OperandType.InlineI8:
                            operandText =
                                BitConverter.ToInt64(
                                    il,
                                    position
                                ).ToString();

                            position +=
                                8;

                            break;

                        case OperandType.ShortInlineR:
                            operandText =
                                BitConverter.ToSingle(
                                    il,
                                    position
                                ).ToString(
                                    "R"
                                );

                            position +=
                                4;

                            break;

                        case OperandType.InlineR:
                            operandText =
                                BitConverter.ToDouble(
                                    il,
                                    position
                                ).ToString(
                                    "R"
                                );

                            position +=
                                8;

                            break;

                        case OperandType.ShortInlineVar:
                            operandText =
                                il[position].ToString();

                            position +=
                                1;

                            break;

                        case OperandType.InlineVar:
                            operandText =
                                BitConverter.ToUInt16(
                                    il,
                                    position
                                ).ToString();

                            position +=
                                2;

                            break;

                        case OperandType.ShortInlineBrTarget:
                        {
                            sbyte delta =
                                unchecked(
                                    (sbyte)il[position]
                                );

                            position +=
                                1;

                            operandText =
                                "IL_" +
                                (
                                    position +
                                    delta
                                ).ToString(
                                    "X4"
                                );

                            break;
                        }

                        case OperandType.InlineBrTarget:
                        {
                            int delta =
                                BitConverter.ToInt32(
                                    il,
                                    position
                                );

                            position +=
                                4;

                            operandText =
                                "IL_" +
                                (
                                    position +
                                    delta
                                ).ToString(
                                    "X4"
                                );

                            break;
                        }

                        case OperandType.InlineSwitch:
                        {
                            int count =
                                BitConverter.ToInt32(
                                    il,
                                    position
                                );

                            position +=
                                4;

                            int baseOffset =
                                position +
                                (
                                    count *
                                    4
                                );

                            List<string> targets =
                                new List<string>();

                            for (
                                int switchIndex = 0;
                                switchIndex < count;
                                switchIndex++
                            )
                            {
                                int delta =
                                    BitConverter.ToInt32(
                                        il,
                                        position
                                    );

                                position +=
                                    4;

                                targets.Add(
                                    "IL_" +
                                    (
                                        baseOffset +
                                        delta
                                    ).ToString(
                                        "X4"
                                    )
                                );
                            }

                            operandText =
                                string.Join(
                                    ",",
                                    targets
                                );

                            break;
                        }

                        case OperandType.InlineString:
                        {
                            int token =
                                BitConverter.ToInt32(
                                    il,
                                    position
                                );

                            position +=
                                4;

                            try
                            {
                                resolvedOperand =
                                    "\"" +
                                    module.ResolveString(
                                        token
                                    ) +
                                    "\"";
                            }
                            catch
                            {
                                resolvedOperand =
                                    "token=0x" +
                                    token.ToString(
                                        "X8"
                                    );
                            }

                            operandText =
                                resolvedOperand;

                            break;
                        }

                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                        case OperandType.InlineSig:
                        {
                            int token =
                                BitConverter.ToInt32(
                                    il,
                                    position
                                );

                            position +=
                                4;

                            try
                            {
                                MemberInfo member =
                                    module.ResolveMember(
                                        token,
                                        typeArguments,
                                        methodArguments
                                    );

                                if (
                                    member != null
                                )
                                {
                                    resolvedOperand =
                                        (
                                            member.DeclaringType != null
                                                ? member.DeclaringType.FullName +
                                                  "::"
                                                : string.Empty
                                        ) +
                                        member.Name;
                                }
                            }
                            catch
                            {
                            }

                            if (
                                string.IsNullOrEmpty(
                                    resolvedOperand
                                )
                            )
                            {
                                resolvedOperand =
                                    "token=0x" +
                                    token.ToString(
                                        "X8"
                                    );
                            }

                            operandText =
                                resolvedOperand;

                            break;
                        }

                        default:
                            operandText =
                                "<operand " +
                                opCode.OperandType +
                                ">";

                            break;
                    }
                }
                catch (Exception ex)
                {
                    operandText =
                        "<decode-error " +
                        ex.GetType().Name +
                        ">";

                    position =
                        il.Length;
                }

                decoded.Add(
                    new DecodedIlInstruction
                    {
                        Offset =
                            offset,
                        ResolvedOperand =
                            resolvedOperand,
                        Text =
                            "IL_" +
                            offset.ToString(
                                "X4"
                            ) +
                            ": " +
                            opCode.Name +
                            (
                                string.IsNullOrEmpty(
                                    operandText
                                )
                                    ? string.Empty
                                    : " " +
                                      operandText
                            )
                    }
                );
            }

            bool[] interesting =
                new bool[decoded.Count];

            for (
                int i = 0;
                i < decoded.Count;
                i++
            )
            {
                string haystack =
                    (
                        decoded[i].Text +
                        " " +
                        decoded[i].ResolvedOperand
                    ).ToLowerInvariant();

                if (
                    haystack.Contains(
                        "underconstruction"
                    ) ||
                    haystack.Contains(
                        "meshbatch"
                    ) ||
                    haystack.Contains(
                        "meshcolor"
                    ) ||
                    haystack.Contains(
                        "prefabref"
                    ) ||
                    haystack.Contains(
                        "renderprefab"
                    ) ||
                    haystack.Contains(
                        "batchinstance"
                    ) ||
                    haystack.Contains(
                        "requiredbatch"
                    )
                )
                {
                    int start =
                        Math.Max(
                            0,
                            i -
                            10
                        );

                    int end =
                        Math.Min(
                            decoded.Count -
                            1,
                            i +
                            10
                        );

                    for (
                        int j = start;
                        j <= end;
                        j++
                    )
                    {
                        interesting[j] =
                            true;
                    }
                }
            }

            ModLog.Checkpoint(
                "NATIVE-IL-PROBE method-begin; method=" +
                method.DeclaringType?.FullName +
                "::" +
                method.Name +
                "; ilBytes=" +
                il.Length +
                "; instructions=" +
                decoded.Count
            );

            int emitted =
                0;

            for (
                int i = 0;
                i < decoded.Count;
                i++
            )
            {
                if (
                    !interesting[i]
                )
                {
                    continue;
                }

                ModLog.Checkpoint(
                    "NATIVE-IL " +
                    method.DeclaringType?.Name +
                    "::" +
                    method.Name +
                    "; " +
                    decoded[i].Text
                );

                emitted++;
            }

            ModLog.Checkpoint(
                "NATIVE-IL-PROBE method-end; method=" +
                method.DeclaringType?.FullName +
                "::" +
                method.Name +
                "; emitted=" +
                emitted
            );
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
            m_DiagnosticUpdateSequence++;

            try
            {
                SetDiagnosticStage(
                    "update.begin",
                    Entity.Null
                );

                ProcessPendingProxyDestroys();
                ProcessPendingUnityDestroys();

                foreach (
                    ConstructionVisual visual
                    in m_Visuals.Values
                )
                {
                    visual.SeenThisFrame =
                        false;
                }

                m_QuerySourcesThisFrame.Clear();

                using NativeArray<Entity> buildings =
                    m_BuildingQuery.ToEntityArray(
                        Allocator.Temp
                    );

                WriteDiagnosticHeartbeatIfDue(
                    buildings.Length
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

                    m_QuerySourcesThisFrame.Add(
                        source
                    );

                    SetDiagnosticStage(
                        "update.building",
                        source
                    );

                    ConstructionVisual visual;

                    if (
                        !m_Visuals.TryGetValue(
                            source,
                            out visual
                        )
                    )
                    {
                        if (
                            !TryRebindConstructionVisual(
                                source,
                                out visual
                            )
                        )
                        {
                            int seenFrames;

                            if (
                                !m_CandidateSeenFrames.TryGetValue(
                                    source,
                                    out seenFrames
                                )
                            )
                            {
                                seenFrames =
                                    0;
                            }

                            seenFrames++;

                            m_CandidateSeenFrames[source] =
                                seenFrames;

                            if (
                                seenFrames <
                                NewConstructionConfirmationFrames
                            )
                            {
                                continue;
                            }

                            m_CandidateSeenFrames.Remove(
                                source
                            );

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
                    }

                    visual.SeenThisFrame =
                        true;

                    visual.MissingFrames =
                        0;

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
                    ConstructionVisual visual =
                        pair.Value;

                    if (
                        !visual.SeenThisFrame
                    )
                    {
                        // V1.43.47.4.3.14: UnderConstruction disappears when the
                        // vanilla building is complete. Keep our animated asset
                        // visible briefly, then hide it and begin scaffold
                        // dismantling from that same hand-off moment.
                        bool sourceStillExists =
                            visual.Source != Entity.Null &&
                            EntityManager.Exists(
                                visual.Source
                            );

                        bool constructionFinished =
                            sourceStillExists &&
                            !EntityManager.HasComponent<UnderConstruction>(
                                visual.Source
                            );

                        if (
                            constructionFinished
                        )
                        {
                            bool dismantlingComplete =
                                UpdateCompletedConstructionVisual(
                                    visual
                                );

                            if (
                                dismantlingComplete
                            )
                            {
                                m_RemoveSources.Add(
                                    pair.Key
                                );
                            }

                            continue;
                        }

                        visual.MissingFrames++;

                        if (
                            visual.MissingFrames ==
                            1
                        )
                        {
                            SuspendConstructionVisual(
                                visual
                            );
                        }

                        if (
                            visual.MissingFrames >=
                            VisualMissingGraceFrames
                        )
                        {
                            m_RemoveSources.Add(
                                pair.Key
                            );
                        }
                    }
                    else
                    {
                        visual.MissingFrames =
                            0;
                    }
                }

                m_RemoveCandidateSources.Clear();

                foreach (
                    KeyValuePair<Entity, int> pair
                    in m_CandidateSeenFrames
                )
                {
                    if (
                        !m_QuerySourcesThisFrame.Contains(
                            pair.Key
                        )
                    )
                    {
                        m_RemoveCandidateSources.Add(
                            pair.Key
                        );
                    }
                }

                for (
                    int i = 0;
                    i < m_RemoveCandidateSources.Count;
                    i++
                )
                {
                    m_CandidateSeenFrames.Remove(
                        m_RemoveCandidateSources[i]
                    );
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

                    SetDiagnosticStage(
                        "update.remove-visual",
                        source
                    );

                    DestroyConstructionVisual(
                        visual
                    );

                    m_Visuals.Remove(
                        source
                    );
                }

                FlushTerrainSplatmapRefreshIfNeeded();

                SetDiagnosticStage(
                    "update.end",
                    Entity.Null
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.37 OnUpdate managed exception; " +
                    BuildDiagnosticContext() +
                    "; exception=" +
                    ex
                );

                throw;
            }
        }

        private void SetDiagnosticStage(
            string stage,
            Entity source
        )
        {
            m_LastDiagnosticStage =
                string.IsNullOrWhiteSpace(stage)
                    ? "unknown"
                    : stage;

            m_LastDiagnosticSource =
                source;
        }

        private string BuildDiagnosticContext()
        {
            string sourceText =
                m_LastDiagnosticSource == Entity.Null
                    ? "null"
                    : m_LastDiagnosticSource.Index +
                      ":" +
                      m_LastDiagnosticSource.Version;

            return
                "update=" +
                m_DiagnosticUpdateSequence +
                "; stage=" +
                m_LastDiagnosticStage +
                "; source=" +
                sourceText +
                "; visuals=" +
                m_Visuals.Count +
                "; pendingProxyDestroys=" +
                m_PendingProxyDestroys.Count +
                "; pendingUnityDestroys=" +
                m_PendingUnityDestroys.Count;
        }

        private void WriteDiagnosticHeartbeatIfDue(
            int queriedBuildings
        )
        {
            float now =
                UnityEngine.Time.unscaledTime;

            if (
                now <
                m_NextDiagnosticHeartbeatTime
            )
            {
                return;
            }

            m_NextDiagnosticHeartbeatTime =
                now +
                DiagnosticHeartbeatInterval;

            int scaffoldRoots =
                0;

            int scaffoldLevels =
                0;

            int scaffoldMeshes =
                0;

            int craneEntities =
                0;

            foreach (
                ConstructionVisual visual
                in m_Visuals.Values
            )
            {
                if (
                    visual == null
                )
                {
                    continue;
                }

                if (
                    visual.ScaffoldRoot != null
                )
                {
                    scaffoldRoots++;
                }

                scaffoldLevels +=
                    visual.ScaffoldLevels == null
                        ? 0
                        : visual.ScaffoldLevels.Count;

                scaffoldMeshes +=
                    visual.ScaffoldMeshes == null
                        ? 0
                        : visual.ScaffoldMeshes.Count;

                if (
                    visual.CraneEntity != Entity.Null
                )
                {
                    craneEntities++;
                }
            }

            ModLog.Diagnostic(
                "HEARTBEAT; " +
                BuildDiagnosticContext() +
                "; queriedBuildings=" +
                queriedBuildings +
                "; scaffoldRoots=" +
                scaffoldRoots +
                "; scaffoldLevels=" +
                scaffoldLevels +
                "; scaffoldMeshes=" +
                scaffoldMeshes +
                "; cranes=" +
                craneEntities
            );
        }

        private bool TryRebindConstructionVisual(
            Entity newSource,
            out ConstructionVisual visual
        )
        {
            visual =
                null;

            if (
                newSource == Entity.Null ||
                !EntityManager.Exists(
                    newSource
                ) ||
                !EntityManager.HasComponent<PrefabRef>(
                    newSource
                )
            )
            {
                return false;
            }

            Entity newPrefab =
                EntityManager.GetComponentData<PrefabRef>(
                    newSource
                ).m_Prefab;

            Entity oldKey =
                Entity.Null;

            foreach (
                KeyValuePair<Entity, ConstructionVisual> pair
                in m_Visuals
            )
            {
                if (
                    pair.Key.Index !=
                    newSource.Index ||
                    pair.Key ==
                    newSource ||
                    pair.Value ==
                    null
                )
                {
                    continue;
                }

                ConstructionVisual candidate =
                    pair.Value;

                if (
                    candidate.Proxy == Entity.Null ||
                    !EntityManager.Exists(
                        candidate.Proxy
                    ) ||
                    !EntityManager.HasComponent<PrefabRef>(
                        candidate.Proxy
                    )
                )
                {
                    continue;
                }

                Entity proxyPrefab =
                    EntityManager.GetComponentData<PrefabRef>(
                        candidate.Proxy
                    ).m_Prefab;

                if (
                    proxyPrefab !=
                    newPrefab
                )
                {
                    continue;
                }

                oldKey =
                    pair.Key;

                visual =
                    candidate;

                break;
            }

            if (
                visual == null ||
                oldKey == Entity.Null
            )
            {
                return false;
            }

            m_Visuals.Remove(
                oldKey
            );

            visual.Source =
                newSource;

            visual.SeenThisFrame =
                true;

            visual.MissingFrames =
                0;

            visual.SourceSurfaceCaptured =
                false;

            visual.LoggedSurfaceSubObjects.Clear();

            m_Visuals.Add(
                newSource,
                visual
            );

            try
            {
                Game.Objects.Transform transform =
                    EntityManager.GetComponentData<Game.Objects.Transform>(
                        newSource
                    );

                if (
                    visual.Proxy != Entity.Null &&
                    EntityManager.Exists(
                        visual.Proxy
                    )
                )
                {
                    EntityManager.SetComponentData(
                        visual.Proxy,
                        transform
                    );

                    EntityManager.SetComponentData(
                        visual.Proxy,
                        EntityManager.GetComponentData<PrefabRef>(
                            newSource
                        )
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
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 visual rebind proxy refresh failed: " +
                    ex.GetType().Name
                );
            }

            ResumeConstructionVisual(
                visual
            );

            ModLog.Checkpoint(
                "VISUAL rebind; oldSource=" +
                oldKey.Index +
                ":" +
                oldKey.Version +
                "; newSource=" +
                newSource.Index +
                ":" +
                newSource.Version +
                "; proxy=" +
                visual.Proxy.Index +
                ":" +
                visual.Proxy.Version +
                "; meshes=" +
                visual.ScaffoldMeshes.Count
            );

            return true;
        }

        private ConstructionVisual CreateConstructionVisual(
            Entity source
        )
        {
            if (
                m_Visuals.Count >=
                MaxSimultaneousConstructionVisuals
            )
            {
                return null;
            }

            try
            {
                ConstructionVisual visual =
                    new ConstructionVisual();

                visual.Source =
                    source;

                SetDiagnosticStage(
                    "create.begin",
                    source
                );

                ModLog.Checkpoint(
                    "CREATE begin; source=" +
                    source.Index +
                    ":" +
                    source.Version
                );

                // V1.43.37: restore the published build's timing, but without
                // its crash-prone global EntityQuery. The source building already
                // exposes its construction surfaces through SubArea, so remove
                // Surface/Batch only here, before our visual is created.
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

                ReadBuildingLotDimensions(
                    prefabRef.m_Prefab,
                    visual
                );

                AnalyseBuildingMeshes(
                    prefabRef.m_Prefab,
                    visual
                );

                // Sand Surface suppression is independent from terrain painting.
                // Run immediately and keep retrying in UpdateConstructionVisual.
                RetryConstructionSandSurfaceRemoval(
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

                TrimRoofOnlyTopLevel(
                    visual
                );

                // V1.43.47.4.3.14: the cutoff path no longer consumes procedural
                // per-floor volume profiles. Keep AnalyseBuildingMeshes for
                // footprint/scaffold geometry, but skip this expensive dead work.

                SetDiagnosticStage(
                    "create.native-proxy",
                    source
                );

                ModLog.Checkpoint(
                    "CREATE native-proxy begin; source=" +
                    source.Index +
                    ":" +
                    source.Version
                );

                CreateNativeProxy(
                    visual,
                    prefabRef
                );

                CreateFoldedBuildingVisual(
                    visual,
                    prefabRef.m_Prefab
                );

                // Roof reconstruction also consumes the principal asset triangles.
                // Clear only after the complete procedural structure has been built.
                visual.StructureTriangleVertices.Clear();

                ModLog.Checkpoint(
                    "CREATE native-proxy end; source=" +
                    source.Index +
                    ":" +
                    source.Version +
                    "; proxy=" +
                    visual.Proxy.Index +
                    ":" +
                    visual.Proxy.Version
                );

                SetDiagnosticStage(
                    "create.scaffold",
                    source
                );

                CreateScaffold(
                    visual
                );

                // V1.43.47.4.3.14: the vanilla Sand Surface is suppressed by
                // the dedicated early lifecycle system. Paint only our dirt
                // brush here and keep it until the full temporary construction
                // visual, including scaffold dismantling, has finished.
                ApplyPublishedTerrainDirt(
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

                ModLog.Checkpoint(
                    "CREATE complete; source=" +
                    source.Index +
                    ":" +
                    source.Version +
                    "; proxy=" +
                    visual.Proxy.Index +
                    ":" +
                    visual.Proxy.Version +
                    "; floors=" +
                    visual.ScaffoldLevels.Count +
                    "; meshes=" +
                    visual.ScaffoldMeshes.Count +
                    "; height=" +
                    visual.BuildingHeight.ToString("0.00")
                );

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
                            Vector3[] vertices =
                                mainMesh.vertices;

                            List<Vector3> transformedVertices =
                                new List<Vector3>(
                                    vertices.Length
                                );

                            int[] mainTriangles =
                                null;

                            try
                            {
                                mainTriangles =
                                    mainMesh.triangles;
                            }
                            catch
                            {
                            }

                            float candidateMinY =
                                float.MaxValue;

                            float candidateMaxY =
                                float.MinValue;

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

                                transformedVertices.Add(
                                    new Vector3(
                                        local.x,
                                        local.y,
                                        local.z
                                    )
                                );

                                candidateMinY =
                                    math.min(
                                        candidateMinY,
                                        local.y
                                    );

                                candidateMaxY =
                                    math.max(
                                        candidateMaxY,
                                        local.y
                                    );

                                globalMinY =
                                    math.min(
                                        globalMinY,
                                        local.y
                                    );
                            }

                            List<Vector3> candidateTriangleVertices =
                                new List<Vector3>();

                            if (
                                mainTriangles != null &&
                                mainTriangles.Length >= 3
                            )
                            {
                                for (
                                    int triangleIndex = 0;
                                    triangleIndex + 2 < mainTriangles.Length;
                                    triangleIndex += 3
                                )
                                {
                                    int index0 =
                                        mainTriangles[triangleIndex];

                                    int index1 =
                                        mainTriangles[triangleIndex + 1];

                                    int index2 =
                                        mainTriangles[triangleIndex + 2];

                                    if (
                                        index0 < 0 ||
                                        index1 < 0 ||
                                        index2 < 0 ||
                                        index0 >= transformedVertices.Count ||
                                        index1 >= transformedVertices.Count ||
                                        index2 >= transformedVertices.Count
                                    )
                                    {
                                        continue;
                                    }

                                    candidateTriangleVertices.Add(
                                        transformedVertices[index0]
                                    );

                                    candidateTriangleVertices.Add(
                                        transformedVertices[index1]
                                    );

                                    candidateTriangleVertices.Add(
                                        transformedVertices[index2]
                                    );
                                }
                            }

                            float candidateHeight =
                                candidateMaxY -
                                candidateMinY;

                            float baseSliceHeight =
                                Mathf.Clamp(
                                    candidateHeight *
                                    0.16f,
                                    0.75f,
                                    2.50f
                                );

                            float baseSliceTop =
                                candidateMinY +
                                baseSliceHeight;

                            List<Vector2> candidatePoints =
                                new List<Vector2>();

                            for (
                                int vertexIndex = 0;
                                vertexIndex < transformedVertices.Count;
                                vertexIndex++
                            )
                            {
                                Vector3 local =
                                    transformedVertices[
                                        vertexIndex
                                    ];

                                if (
                                    local.y >
                                    baseSliceTop
                                )
                                {
                                    continue;
                                }

                                candidatePoints.Add(
                                    new Vector2(
                                        local.x,
                                        local.z
                                    )
                                );
                            }

                            if (
                                candidatePoints.Count <
                                3
                            )
                            {
                                for (
                                    int vertexIndex = 0;
                                    vertexIndex < transformedVertices.Count;
                                    vertexIndex++
                                )
                                {
                                    Vector3 local =
                                        transformedVertices[
                                            vertexIndex
                                        ];

                                    candidatePoints.Add(
                                        new Vector2(
                                            local.x,
                                            local.z
                                        )
                                    );
                                }
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
                                candidateTriangleVertices,
                                renderPrefabName,
                                candidateHeight,
                                candidateMinY,
                                visual.BuildingHeight,
                                visual.BuildingSize
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

            if (
                globalMinY != float.MaxValue
            )
            {
                visual.StructureGeometryBaseY =
                    globalMinY;
            }

            FootprintCandidate selectedCandidate =
                SelectFootprintCandidate(
                    footprintCandidates
                );

            if (
                selectedCandidate != null
            )
            {
                visual.StructureTriangleVertices =
                    selectedCandidate.PrincipalTriangleVertices != null
                        ? new List<Vector3>(
                            selectedCandidate.PrincipalTriangleVertices
                        )
                        : new List<Vector3>();

                visual.StructureGeometryBaseY =
                    selectedCandidate.MinY;

                globalMinY =
                    selectedCandidate.MinY;

                bool useStableSmallBuildingFootprint =
                    visual.BuildingHeight <=
                    SmallBuildingStableFootprintMaximumHeight &&
                    selectedCandidate.Area <=
                    SmallBuildingStableFootprintMaximumArea;

                bool useBoundingRectangle =
                    selectedCandidate.Compactness >=
                    0.85f ||
                    useStableSmallBuildingFootprint;

                if (
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
                    $"mode={(useBoundingRectangle ? "rectangle" : "hull")}"
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
            List<Vector3> principalTriangleVertices,
            string prefabName,
            float candidateHeight,
            float candidateMinY,
            float buildingHeight,
            float3 buildingSize
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

            float minimumStructuralHeight =
                Mathf.Max(
                    2.50f,
                    buildingHeight *
                    0.45f
                );

            if (
                candidateHeight <
                minimumStructuralHeight ||
                IsLikelyNonBuildingFootprintPrefab(
                    prefabName
                )
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

            float maximumExpectedWidth =
                Mathf.Max(
                    2f,
                    buildingSize.x *
                    1.12f
                );

            float maximumExpectedDepth =
                Mathf.Max(
                    2f,
                    buildingSize.z *
                    1.12f
                );

            if (
                width >
                maximumExpectedWidth ||
                depth >
                maximumExpectedDepth
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
                principalTriangleVertices != null
            )
            {
                candidate.PrincipalTriangleVertices =
                    new List<Vector3>(
                        principalTriangleVertices
                    );
            }

            candidate.PrefabName =
                prefabName;

            candidate.Area =
                area;

            candidate.Compactness =
                compactness;

            candidate.Height =
                candidateHeight;

            candidate.MinY =
                candidateMinY;

            candidate.HeightCoverage =
                Mathf.Clamp01(
                    candidateHeight /
                    Mathf.Max(
                        buildingHeight,
                        0.01f
                    )
                );

            candidate.Score =
                area *
                compactness *
                Mathf.Lerp(
                    0.20f,
                    1f,
                    candidate.HeightCoverage
                );

            candidates.Add(
                candidate
            );
        }

        private static bool IsLikelyNonBuildingFootprintPrefab(
            string prefabName
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    prefabName
                )
            )
            {
                return false;
            }

            string normalized =
                prefabName.ToLowerInvariant();

            return
                normalized.Contains(
                    "surface"
                ) ||
                normalized.Contains(
                    "ground"
                ) ||
                normalized.Contains(
                    "terrain"
                ) ||
                normalized.Contains(
                    "decal"
                ) ||
                normalized.Contains(
                    "fence"
                ) ||
                normalized.Contains(
                    "placeholder"
                ) ||
                normalized.Contains(
                    "parking"
                ) ||
                normalized.Contains(
                    "lot"
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

            float estimatedTop =
                buildingHeight;

            if (
                rows.Count >=
                2
            )
            {
                float lastSpacing =
                    rows[
                        rows.Count -
                        1
                    ] -
                    rows[
                        rows.Count -
                        2
                    ];

                if (
                    lastSpacing >
                    0.5f
                )
                {
                    estimatedTop =
                        Mathf.Min(
                            buildingHeight,
                            rows[
                                rows.Count -
                                1
                            ] +
                            lastSpacing *
                            0.5f
                        );
                }
            }

            boundaries.Add(
                estimatedTop
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

        private void TrimRoofOnlyTopLevel(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.FloorBoundaries == null ||
                visual.FloorBoundaries.Count < 3
            )
            {
                return;
            }

            List<float> boundaries =
                visual.FloorBoundaries;

            int floorCount =
                boundaries.Count -
                1;

            List<float> referenceHeights =
                new List<float>();

            for (
                int i = 0;
                i < floorCount - 1;
                i++
            )
            {
                float height =
                    boundaries[i + 1] -
                    boundaries[i];

                if (
                    height >= 1.75f &&
                    height <= 6.5f
                )
                {
                    referenceHeights.Add(
                        height
                    );
                }
            }

            if (
                referenceHeights.Count == 0
            )
            {
                return;
            }

            referenceHeights.Sort();

            float medianHeight =
                referenceHeights[
                    referenceHeights.Count /
                    2
                ];

            float topHeight =
                boundaries[
                    boundaries.Count -
                    1
                ] -
                boundaries[
                    boundaries.Count -
                    2
                ];

            bool roofLike =
                topHeight <
                    medianHeight *
                    0.72f &&
                topHeight <
                    2.45f;

            if (
                !roofLike
            )
            {
                return;
            }

            float removedTop =
                boundaries[
                    boundaries.Count -
                    1
                ];

            float structuralTop =
                boundaries[
                    boundaries.Count -
                    2
                ];

            boundaries.RemoveAt(
                boundaries.Count -
                1
            );

            ModLog.Checkpoint(
                "STRUCTURE-ROOF-TRIM; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; removedTop=" +
                removedTop.ToString(
                    "0.00"
                ) +
                "; structuralTop=" +
                structuralTop.ToString(
                    "0.00"
                ) +
                "; roofBand=" +
                topHeight.ToString(
                    "0.00"
                ) +
                "; referenceFloor=" +
                medianHeight.ToString(
                    "0.00"
                )
            );
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

        private Material TryCloneDirectPrefabMaterial(
            object renderPrefab,
            Entity renderPrefabEntity
        )
        {
            if (
                renderPrefab == null
            )
            {
                return null;
            }

            try
            {
                Type type =
                    renderPrefab.GetType();

                while (
                    type != null &&
                    type != typeof(object)
                )
                {
                    FieldInfo[] fields =
                        type.GetFields(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    for (
                        int i = 0;
                        i < fields.Length;
                        i++
                    )
                    {
                        FieldInfo field =
                            fields[i];

                        string fieldName =
                            field.Name ??
                            string.Empty;

                        if (
                            fieldName.IndexOf(
                                "material",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                        {
                            continue;
                        }

                        object value =
                            field.GetValue(
                                renderPrefab
                            );

                        Material directMaterial =
                            ExtractDirectUnityMaterial(
                                value
                            );

                        if (
                            directMaterial != null
                        )
                        {
                            Material displayMaterial =
                                CreateBuildingDisplayMaterialFromSource(
                                    directMaterial,
                                    renderPrefabEntity,
                                    fieldName
                                );

                            if (
                                displayMaterial != null
                            )
                            {
                                return displayMaterial;
                            }
                        }
                    }

                    PropertyInfo[] properties =
                        type.GetProperties(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    for (
                        int i = 0;
                        i < properties.Length;
                        i++
                    )
                    {
                        PropertyInfo property =
                            properties[i];

                        string propertyName =
                            property.Name ??
                            string.Empty;

                        if (
                            propertyName.IndexOf(
                                "material",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0 ||
                            !property.CanRead ||
                            property.GetIndexParameters().Length != 0
                        )
                        {
                            continue;
                        }

                        object value;

                        try
                        {
                            value =
                                property.GetValue(
                                    renderPrefab,
                                    null
                                );
                        }
                        catch
                        {
                            continue;
                        }

                        Material directMaterial =
                            ExtractDirectUnityMaterial(
                                value
                            );

                        if (
                            directMaterial != null
                        )
                        {
                            Material displayMaterial =
                                CreateBuildingDisplayMaterialFromSource(
                                    directMaterial,
                                    renderPrefabEntity,
                                    propertyName
                                );

                            if (
                                displayMaterial != null
                            )
                            {
                                return displayMaterial;
                            }
                        }
                    }

                    type =
                        type.BaseType;
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 prefab material inspection skipped: " +
                    ex.GetType().Name
                );
            }

            Material reconstructedMaterial =
                TryCreateOpaqueMaterialFromPrefabResources(
                    renderPrefab,
                    renderPrefabEntity
                );

            if (
                reconstructedMaterial != null
            )
            {
                return reconstructedMaterial;
            }

            return null;
        }

        private Material TryCreateOpaqueMaterialFromPrefabResources(
            object renderPrefab,
            Entity renderPrefabEntity
        )
        {
            if (
                renderPrefab == null
            )
            {
                return null;
            }

            try
            {
                Material nestedMaterial = null;
                Texture baseColorTexture = null;
                Texture normalTexture = null;
                Texture maskTexture = null;
                Texture emissiveTexture = null;
                int remainingNodes = 192;

                HashSet<object> visited =
                    new HashSet<object>();

                ScanPrefabRenderResources(
                    renderPrefab,
                    renderPrefab.GetType().Name,
                    0,
                    visited,
                    ref remainingNodes,
                    ref nestedMaterial,
                    ref baseColorTexture,
                    ref normalTexture,
                    ref maskTexture,
                    ref emissiveTexture
                );

                Material material = null;

                if (
                    nestedMaterial != null
                )
                {
                    material =
                        CreateBuildingDisplayMaterialFromSource(
                            nestedMaterial,
                            renderPrefabEntity,
                            "nestedMaterial"
                        );
                }
                else if (
                    baseColorTexture != null ||
                    normalTexture != null ||
                    maskTexture != null ||
                    emissiveTexture != null
                )
                {
                    if (
                        m_BuildingConstructionMaterial != null
                    )
                    {
                        material =
                            new Material(
                                m_BuildingConstructionMaterial
                            );
                    }
                    else
                    {
                        Shader shader =
                            Shader.Find(
                                "HDRP/Lit"
                            );

                        if (
                            shader == null
                        )
                        {
                            shader =
                                Shader.Find(
                                    "Standard"
                                );
                        }

                        if (
                            shader != null
                        )
                        {
                            material =
                                new Material(
                                    shader
                                );
                        }
                    }
                }

                if (
                    material == null
                )
                {
                    ModLog.Checkpoint(
                        "BUILDING-FOLD material resources unavailable; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version
                    );

                    return null;
                }

                material.name =
                    "ConstructionAnimation_BuildingResourceMaterial_" +
                    renderPrefabEntity.Index +
                    "_" +
                    renderPrefabEntity.Version;

                ApplyRecoveredBuildingTextures(
                    material,
                    baseColorTexture,
                    normalTexture,
                    maskTexture,
                    emissiveTexture
                );

                ConfigureOpaqueDepthMaterial(
                    material
                );

                ForceMaterialAlphaOne(
                    material
                );

                ValidateHdrpMaterial(
                    material,
                    "building-resource-copy"
                );

                ModLog.Checkpoint(
                    "BUILDING-FOLD material resources recovered; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; nestedMaterial=" +
                    (nestedMaterial != null) +
                    "; base=" +
                    (baseColorTexture != null) +
                    "; normal=" +
                    (normalTexture != null) +
                    "; mask=" +
                    (maskTexture != null) +
                    "; emissive=" +
                    (emissiveTexture != null)
                );

                return material;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 prefab resource scan skipped: " +
                    ex.GetType().Name
                );

                return null;
            }
        }

        private static void ScanPrefabRenderResources(
            object value,
            string path,
            int depth,
            HashSet<object> visited,
            ref int remainingNodes,
            ref Material nestedMaterial,
            ref Texture baseColorTexture,
            ref Texture normalTexture,
            ref Texture maskTexture,
            ref Texture emissiveTexture
        )
        {
            if (
                value == null ||
                remainingNodes <= 0 ||
                depth > 4
            )
            {
                return;
            }

            remainingNodes--;

            Material material =
                value as Material;

            if (
                material != null
            )
            {
                if (
                    nestedMaterial == null
                )
                {
                    nestedMaterial =
                        material;
                }

                return;
            }

            Texture texture =
                value as Texture;

            if (
                texture != null
            )
            {
                AssignRecoveredTexture(
                    path,
                    texture,
                    ref baseColorTexture,
                    ref normalTexture,
                    ref maskTexture,
                    ref emissiveTexture
                );

                return;
            }

            Type type =
                value.GetType();

            if (
                type.IsPrimitive ||
                type.IsEnum ||
                value is string ||
                value is decimal ||
                value is Type ||
                value is Delegate ||
                value is Entity
            )
            {
                return;
            }

            if (
                !type.IsValueType
            )
            {
                if (
                    visited.Contains(
                        value
                    )
                )
                {
                    return;
                }

                visited.Add(
                    value
                );
            }

            System.Collections.IEnumerable enumerable =
                value as System.Collections.IEnumerable;

            if (
                enumerable != null &&
                !(value is string)
            )
            {
                int itemCount = 0;

                try
                {
                    foreach (
                        object item in enumerable
                    )
                    {
                        ScanPrefabRenderResources(
                            item,
                            path +
                            "[]",
                            depth + 1,
                            visited,
                            ref remainingNodes,
                            ref nestedMaterial,
                            ref baseColorTexture,
                            ref normalTexture,
                            ref maskTexture,
                            ref emissiveTexture
                        );

                        itemCount++;

                        if (
                            itemCount >= 16 ||
                            remainingNodes <= 0
                        )
                        {
                            break;
                        }
                    }
                }
                catch
                {
                }

                return;
            }

            Type currentType =
                type;

            while (
                currentType != null &&
                currentType != typeof(object) &&
                remainingNodes > 0
            )
            {
                FieldInfo[] fields;

                try
                {
                    fields =
                        currentType.GetFields(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );
                }
                catch
                {
                    fields =
                        new FieldInfo[0];
                }

                for (
                    int i = 0;
                    i < fields.Length &&
                    remainingNodes > 0;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

                    object childValue;

                    try
                    {
                        childValue =
                            field.GetValue(
                                value
                            );
                    }
                    catch
                    {
                        continue;
                    }

                    ScanPrefabRenderResources(
                        childValue,
                        path +
                        "." +
                        field.Name,
                        depth + 1,
                        visited,
                        ref remainingNodes,
                        ref nestedMaterial,
                        ref baseColorTexture,
                        ref normalTexture,
                        ref maskTexture,
                        ref emissiveTexture
                    );
                }

                currentType =
                    currentType.BaseType;
            }
        }

        private static void AssignRecoveredTexture(
            string path,
            Texture texture,
            ref Texture baseColorTexture,
            ref Texture normalTexture,
            ref Texture maskTexture,
            ref Texture emissiveTexture
        )
        {
            if (
                texture == null
            )
            {
                return;
            }

            string normalized =
                (
                    path ??
                    string.Empty
                ).ToLowerInvariant();

            if (
                normalized.Contains(
                    "normal"
                )
            )
            {
                if (
                    normalTexture == null
                )
                {
                    normalTexture =
                        texture;
                }

                return;
            }

            if (
                normalized.Contains(
                    "mask"
                ) ||
                normalized.Contains(
                    "metal"
                ) ||
                normalized.Contains(
                    "rough"
                ) ||
                normalized.Contains(
                    "smooth"
                ) ||
                normalized.Contains(
                    "occlusion"
                )
            )
            {
                if (
                    maskTexture == null
                )
                {
                    maskTexture =
                        texture;
                }

                return;
            }

            if (
                normalized.Contains(
                    "emiss"
                )
            )
            {
                if (
                    emissiveTexture == null
                )
                {
                    emissiveTexture =
                        texture;
                }

                return;
            }

            if (
                normalized.Contains(
                    "base"
                ) ||
                normalized.Contains(
                    "albedo"
                ) ||
                normalized.Contains(
                    "diffuse"
                ) ||
                normalized.Contains(
                    "color"
                ) ||
                normalized.Contains(
                    "colour"
                )
            )
            {
                if (
                    baseColorTexture == null
                )
                {
                    baseColorTexture =
                        texture;
                }

                return;
            }

            if (
                baseColorTexture == null
            )
            {
                baseColorTexture =
                    texture;
            }
        }

        private static void ApplyRecoveredBuildingTextures(
            Material material,
            Texture baseColorTexture,
            Texture normalTexture,
            Texture maskTexture,
            Texture emissiveTexture
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            if (
                baseColorTexture != null
            )
            {
                SetTextureIfPresent(
                    material,
                    "_BaseColorMap",
                    baseColorTexture
                );

                SetTextureIfPresent(
                    material,
                    "_BaseMap",
                    baseColorTexture
                );

                SetTextureIfPresent(
                    material,
                    "_UnlitColorMap",
                    baseColorTexture
                );

                SetTextureIfPresent(
                    material,
                    "_MainTex",
                    baseColorTexture
                );
            }

            if (
                normalTexture != null
            )
            {
                SetTextureIfPresent(
                    material,
                    "_NormalMap",
                    normalTexture
                );

                SetTextureIfPresent(
                    material,
                    "_BumpMap",
                    normalTexture
                );

                material.EnableKeyword(
                    "_NORMALMAP"
                );
            }

            if (
                maskTexture != null
            )
            {
                SetTextureIfPresent(
                    material,
                    "_MaskMap",
                    maskTexture
                );
            }

            if (
                emissiveTexture != null
            )
            {
                SetTextureIfPresent(
                    material,
                    "_EmissiveColorMap",
                    emissiveTexture
                );

                SetTextureIfPresent(
                    material,
                    "_EmissionMap",
                    emissiveTexture
                );

                material.EnableKeyword(
                    "_EMISSIVE_COLOR_MAP"
                );
            }
        }

        private Material CreateBuildingDisplayMaterialFromSource(
            Material sourceMaterial,
            Entity renderPrefabEntity,
            string memberName
        )
        {
            if (
                sourceMaterial == null
            )
            {
                return null;
            }

            Material displayMaterial = null;

            if (
                m_BuildingConstructionMaterial != null
            )
            {
                displayMaterial =
                    new Material(
                        m_BuildingConstructionMaterial
                    );
            }
            else
            {
                Shader shader =
                    Shader.Find(
                        "HDRP/Unlit"
                    );

                if (
                    shader == null
                )
                {
                    shader =
                        Shader.Find(
                            "Unlit/Texture"
                        );
                }

                if (
                    shader == null
                )
                {
                    shader =
                        Shader.Find(
                            "Unlit/Color"
                        );
                }

                if (
                    shader != null
                )
                {
                    displayMaterial =
                        new Material(
                            shader
                        );
                }
            }

            if (
                displayMaterial == null
            )
            {
                return null;
            }

            displayMaterial.name =
                "ConstructionAnimation_BuildingDisplayMaterial_" +
                renderPrefabEntity.Index +
                "_" +
                renderPrefabEntity.Version;

            Texture baseColorTexture =
                GetFirstTexture(
                    sourceMaterial,
                    "_UnlitColorMap",
                    "_BaseColorMap",
                    "_BaseMap",
                    "_MainTex"
                );

            Texture normalTexture =
                GetFirstTexture(
                    sourceMaterial,
                    "_NormalMap",
                    "_BumpMap"
                );

            Texture maskTexture =
                GetFirstTexture(
                    sourceMaterial,
                    "_MaskMap",
                    "_MetallicGlossMap"
                );

            Texture emissiveTexture =
                GetFirstTexture(
                    sourceMaterial,
                    "_EmissiveColorMap",
                    "_EmissionMap"
                );

            ApplyRecoveredBuildingTextures(
                displayMaterial,
                baseColorTexture,
                normalTexture,
                maskTexture,
                emissiveTexture
            );

            TryCopyMaterialColor(
                sourceMaterial,
                displayMaterial
            );

            ConfigureOpaqueDepthMaterial(
                displayMaterial
            );

            ForceMaterialAlphaOne(
                displayMaterial
            );

            ValidateHdrpMaterial(
                displayMaterial,
                "building-display-copy"
            );

            ModLog.Checkpoint(
                "BUILDING-FOLD material copied; renderPrefab=" +
                renderPrefabEntity.Index +
                ":" +
                renderPrefabEntity.Version +
                "; member=" +
                memberName +
                "; base=" +
                (baseColorTexture != null) +
                "; normal=" +
                (normalTexture != null) +
                "; mask=" +
                (maskTexture != null) +
                "; emissive=" +
                (emissiveTexture != null)
            );

            return displayMaterial;
        }

        private static Texture GetFirstTexture(
            Material sourceMaterial,
            params string[] propertyNames
        )
        {
            if (
                sourceMaterial == null ||
                sourceMaterial.shader == null
            )
            {
                return null;
            }

            try
            {
                Shader shader =
                    sourceMaterial.shader;

                int propertyCount =
                    shader.GetPropertyCount();

                List<string> texturePropertyNames =
                    new List<string>();

                for (
                    int propertyIndex = 0;
                    propertyIndex < propertyCount;
                    propertyIndex++
                )
                {
                    if (
                        shader.GetPropertyType(
                            propertyIndex
                        ) !=
                        UnityEngine.Rendering.ShaderPropertyType.Texture
                    )
                    {
                        continue;
                    }

                    string actualName =
                        shader.GetPropertyName(
                            propertyIndex
                        );

                    if (
                        string.IsNullOrEmpty(
                            actualName
                        )
                    )
                    {
                        continue;
                    }

                    texturePropertyNames.Add(
                        actualName
                    );

                    if (
                        propertyNames != null
                    )
                    {
                        for (
                            int requestedIndex = 0;
                            requestedIndex < propertyNames.Length;
                            requestedIndex++
                        )
                        {
                            string requestedName =
                                propertyNames[
                                    requestedIndex
                                ];

                            if (
                                !string.Equals(
                                    actualName,
                                    requestedName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                continue;
                            }

                            Texture exactTexture =
                                sourceMaterial.GetTexture(
                                    shader.GetPropertyNameId(
                                        propertyIndex
                                    )
                                );

                            if (
                                exactTexture != null
                            )
                            {
                                return exactTexture;
                            }
                        }
                    }
                }

                bool wantsNormal =
                    ContainsRequestedSemantic(
                        propertyNames,
                        "normal",
                        "bump"
                    );

                bool wantsMask =
                    ContainsRequestedSemantic(
                        propertyNames,
                        "mask",
                        "metallic",
                        "rough",
                        "smooth",
                        "occlusion"
                    );

                bool wantsEmissive =
                    ContainsRequestedSemantic(
                        propertyNames,
                        "emissive",
                        "emission"
                    );

                Texture firstUsable =
                    null;

                for (
                    int propertyIndex = 0;
                    propertyIndex < propertyCount;
                    propertyIndex++
                )
                {
                    if (
                        shader.GetPropertyType(
                            propertyIndex
                        ) !=
                        UnityEngine.Rendering.ShaderPropertyType.Texture
                    )
                    {
                        continue;
                    }

                    string actualName =
                        shader.GetPropertyName(
                            propertyIndex
                        ) ??
                        string.Empty;

                    Texture texture =
                        sourceMaterial.GetTexture(
                            shader.GetPropertyNameId(
                                propertyIndex
                            )
                        );

                    if (
                        texture == null
                    )
                    {
                        continue;
                    }

                    string normalized =
                        actualName.ToLowerInvariant();

                    if (
                        firstUsable == null
                    )
                    {
                        firstUsable =
                            texture;
                    }

                    if (
                        wantsNormal &&
                        (
                            normalized.Contains(
                                "normal"
                            ) ||
                            normalized.Contains(
                                "bump"
                            ) ||
                            normalized.Contains(
                                "nrm"
                            )
                        )
                    )
                    {
                        return texture;
                    }

                    if (
                        wantsMask &&
                        (
                            normalized.Contains(
                                "mask"
                            ) ||
                            normalized.Contains(
                                "metal"
                            ) ||
                            normalized.Contains(
                                "rough"
                            ) ||
                            normalized.Contains(
                                "smooth"
                            ) ||
                            normalized.Contains(
                                "occlusion"
                            ) ||
                            normalized.Contains(
                                "orm"
                            )
                        )
                    )
                    {
                        return texture;
                    }

                    if (
                        wantsEmissive &&
                        (
                            normalized.Contains(
                                "emiss"
                            ) ||
                            normalized.Contains(
                                "glow"
                            )
                        )
                    )
                    {
                        return texture;
                    }

                    if (
                        !wantsNormal &&
                        !wantsMask &&
                        !wantsEmissive &&
                        (
                            normalized.Contains(
                                "base"
                            ) ||
                            normalized.Contains(
                                "albedo"
                            ) ||
                            normalized.Contains(
                                "diffuse"
                            ) ||
                            normalized.Contains(
                                "color"
                            ) ||
                            normalized.Contains(
                                "colour"
                            )
                        ) &&
                        !normalized.Contains(
                            "normal"
                        ) &&
                        !normalized.Contains(
                            "mask"
                        ) &&
                        !normalized.Contains(
                            "emiss"
                        )
                    )
                    {
                        return texture;
                    }
                }

                if (
                    !wantsNormal &&
                    !wantsMask &&
                    !wantsEmissive
                )
                {
                    return firstUsable;
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 texture property scan failed: " +
                    ex.GetType().Name
                );
            }

            return null;
        }

        private static bool ContainsRequestedSemantic(
            string[] propertyNames,
            params string[] semanticTerms
        )
        {
            if (
                propertyNames == null ||
                semanticTerms == null
            )
            {
                return false;
            }

            for (
                int propertyIndex = 0;
                propertyIndex < propertyNames.Length;
                propertyIndex++
            )
            {
                string propertyName =
                    propertyNames[
                        propertyIndex
                    ] ??
                    string.Empty;

                for (
                    int termIndex = 0;
                    termIndex < semanticTerms.Length;
                    termIndex++
                )
                {
                    if (
                        propertyName.IndexOf(
                            semanticTerms[
                                termIndex
                            ],
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0
                    )
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string DescribeMaterialTextureProperties(
            Material material
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return "none";
            }

            try
            {
                List<string> entries =
                    new List<string>();

                Shader shader =
                    material.shader;

                int propertyCount =
                    shader.GetPropertyCount();

                for (
                    int propertyIndex = 0;
                    propertyIndex < propertyCount;
                    propertyIndex++
                )
                {
                    if (
                        shader.GetPropertyType(
                            propertyIndex
                        ) !=
                        UnityEngine.Rendering.ShaderPropertyType.Texture
                    )
                    {
                        continue;
                    }

                    string name =
                        shader.GetPropertyName(
                            propertyIndex
                        );

                    Texture texture =
                        material.GetTexture(
                            shader.GetPropertyNameId(
                                propertyIndex
                            )
                        );

                    entries.Add(
                        name +
                        "=" +
                        (
                            texture != null
                                ? texture.name
                                : "null"
                        )
                    );
                }

                return string.Join(
                    ",",
                    entries.ToArray()
                );
            }
            catch
            {
                return "unreadable";
            }
        }

        private static void TryCopyMaterialColor(
            Material sourceMaterial,
            Material destinationMaterial
        )
        {
            if (
                sourceMaterial == null ||
                destinationMaterial == null
            )
            {
                return;
            }

            string[] propertyNames =
                new string[]
                {
                    "_BaseColor",
                    "_UnlitColor",
                    "_Color"
                };

            for (
                int i = 0;
                i < propertyNames.Length;
                i++
            )
            {
                string propertyName =
                    propertyNames[i];

                if (
                    !sourceMaterial.HasProperty(
                        propertyName
                    )
                )
                {
                    continue;
                }

                try
                {
                    UnityEngine.Color color =
                        sourceMaterial.GetColor(
                            propertyName
                        );

                    SetMaterialColor(
                        destinationMaterial,
                        new UnityEngine.Color(
                            color.r,
                            color.g,
                            color.b,
                            1f
                        )
                    );

                    return;
                }
                catch
                {
                }
            }
        }

        private static void SetTextureIfPresent(
            Material material,
            string propertyName,
            Texture texture
        )
        {
            if (
                material != null &&
                texture != null &&
                material.HasProperty(
                    propertyName
                )
            )
            {
                material.SetTexture(
                    propertyName,
                    texture
                );
            }
        }

        private static Material ExtractDirectUnityMaterial(
            object value
        )
        {
            if (
                value == null
            )
            {
                return null;
            }

            Material material =
                value as Material;

            if (
                material != null
            )
            {
                return material;
            }

            Material[] materials =
                value as Material[];

            if (
                materials != null
            )
            {
                for (
                    int i = 0;
                    i < materials.Length;
                    i++
                )
                {
                    if (
                        materials[i] != null
                    )
                    {
                        return materials[i];
                    }
                }
            }

            Array array =
                value as Array;

            if (
                array != null
            )
            {
                for (
                    int i = 0;
                    i < array.Length;
                    i++
                )
                {
                    Material arrayMaterial =
                        array.GetValue(i) as Material;

                    if (
                        arrayMaterial != null
                    )
                    {
                        return arrayMaterial;
                    }
                }
            }

            return null;
        }

        private void ProbeBuildingRenderPrefab(
            object renderPrefab,
            GeometryAsset geometryAsset,
            Entity renderPrefabEntity
        )
        {
            if (
                renderPrefab == null ||
                geometryAsset == null ||
                m_BuildingRenderProbeCount >= MaxBuildingRenderProbes ||
                m_LoggedBuildingRenderPrefabs.Contains(
                    renderPrefabEntity.Index
                )
            )
            {
                return;
            }

            m_LoggedBuildingRenderPrefabs.Add(
                renderPrefabEntity.Index
            );

            m_BuildingRenderProbeCount++;

            ModLog.Checkpoint(
                "BUILDING-RENDER-PROBE begin; renderPrefab=" +
                renderPrefabEntity.Index +
                ":" +
                renderPrefabEntity.Version +
                "; prefabType=" +
                renderPrefab.GetType().FullName +
                "; geometryType=" +
                geometryAsset.GetType().FullName
            );

            ProbeObjectMembers(
                renderPrefab,
                "prefab",
                0,
                96
            );

            ProbeBuildingSurfaceAssets(
                renderPrefab,
                renderPrefabEntity
            );

            ProbeObjectMembers(
                geometryAsset,
                "geometry",
                0,
                96
            );

            ModLog.Checkpoint(
                "BUILDING-RENDER-PROBE end; renderPrefab=" +
                renderPrefabEntity.Index +
                ":" +
                renderPrefabEntity.Version
            );
        }

        private static void ProbeBuildingSurfaceAssets(
            object renderPrefab,
            Entity renderPrefabEntity
        )
        {
            if (
                renderPrefab == null
            )
            {
                return;
            }

            try
            {
                PropertyInfo surfaceAssetsProperty =
                    renderPrefab.GetType().GetProperty(
                        "surfaceAssets",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                if (
                    surfaceAssetsProperty == null ||
                    !surfaceAssetsProperty.CanRead
                )
                {
                    ModLog.Checkpoint(
                        "BUILDING-SURFACE-PROBE unavailable; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version
                    );

                    return;
                }

                object enumerableObject =
                    surfaceAssetsProperty.GetValue(
                        renderPrefab,
                        null
                    );

                System.Collections.IEnumerable enumerable =
                    enumerableObject as System.Collections.IEnumerable;

                if (
                    enumerable == null
                )
                {
                    ModLog.Checkpoint(
                        "BUILDING-SURFACE-PROBE not-enumerable; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version
                    );

                    return;
                }

                int surfaceIndex = 0;

                foreach (
                    object surfaceAsset in enumerable
                )
                {
                    if (
                        surfaceIndex >= 8
                    )
                    {
                        break;
                    }

                    ModLog.Checkpoint(
                        "BUILDING-SURFACE-PROBE begin; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version +
                        "; surfaceIndex=" +
                        surfaceIndex +
                        "; surfaceType=" +
                        (
                            surfaceAsset != null
                                ? surfaceAsset.GetType().FullName
                                : "null"
                        ) +
                        "; surface=" +
                        FormatProbeValue(
                            surfaceAsset
                        )
                    );

                    if (
                        surfaceAsset != null
                    )
                    {
                        ProbeObjectMembers(
                            surfaceAsset,
                            "surface[" +
                            surfaceIndex +
                            "]",
                            0,
                            128
                        );
                    }

                    ModLog.Checkpoint(
                        "BUILDING-SURFACE-PROBE end; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version +
                        "; surfaceIndex=" +
                        surfaceIndex
                    );

                    surfaceIndex++;
                }

                ModLog.Checkpoint(
                    "BUILDING-SURFACE-PROBE complete; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; count=" +
                    surfaceIndex
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 surface probe skipped: " +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private static void ProbeObjectMembers(
            object target,
            string label,
            int depth,
            int maxMembers
        )
        {
            if (
                target == null ||
                depth > 1 ||
                maxMembers <= 0
            )
            {
                return;
            }

            try
            {
                Type type =
                    target.GetType();

                int written = 0;

                while (
                    type != null &&
                    type != typeof(object) &&
                    written < maxMembers
                )
                {
                    FieldInfo[] fields =
                        type.GetFields(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    for (
                        int i = 0;
                        i < fields.Length &&
                        written < maxMembers;
                        i++
                    )
                    {
                        FieldInfo field =
                            fields[i];

                        object value = null;
                        string valueText = "<unreadable>";

                        try
                        {
                            value =
                                field.GetValue(
                                    target
                                );

                            valueText =
                                FormatProbeValue(
                                    value
                                );
                        }
                        catch
                        {
                        }

                        ModLog.Checkpoint(
                            "BUILDING-RENDER-PROBE " +
                            label +
                            ".field; name=" +
                            field.Name +
                            "; declared=" +
                            field.FieldType.FullName +
                            "; valueType=" +
                            (
                                value != null
                                    ? value.GetType().FullName
                                    : "null"
                            ) +
                            "; value=" +
                            valueText
                        );

                        written++;

                        if (
                            depth == 0 &&
                            ShouldProbeNestedMember(
                                field.Name,
                                field.FieldType,
                                value
                            )
                        )
                        {
                            ProbeObjectMembers(
                                value,
                                label + "." + field.Name,
                                depth + 1,
                                32
                            );
                        }
                    }

                    PropertyInfo[] properties =
                        type.GetProperties(
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    for (
                        int i = 0;
                        i < properties.Length &&
                        written < maxMembers;
                        i++
                    )
                    {
                        PropertyInfo property =
                            properties[i];

                        if (
                            !property.CanRead ||
                            property.GetIndexParameters().Length != 0
                        )
                        {
                            continue;
                        }

                        object value = null;
                        string valueText = "<unreadable>";

                        try
                        {
                            value =
                                property.GetValue(
                                    target,
                                    null
                                );

                            valueText =
                                FormatProbeValue(
                                    value
                                );
                        }
                        catch
                        {
                        }

                        ModLog.Checkpoint(
                            "BUILDING-RENDER-PROBE " +
                            label +
                            ".property; name=" +
                            property.Name +
                            "; declared=" +
                            property.PropertyType.FullName +
                            "; valueType=" +
                            (
                                value != null
                                    ? value.GetType().FullName
                                    : "null"
                            ) +
                            "; value=" +
                            valueText
                        );

                        written++;

                        if (
                            depth == 0 &&
                            ShouldProbeNestedMember(
                                property.Name,
                                property.PropertyType,
                                value
                            )
                        )
                        {
                            ProbeObjectMembers(
                                value,
                                label + "." + property.Name,
                                depth + 1,
                                32
                            );
                        }
                    }

                    type =
                        type.BaseType;
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 render probe skipped: " +
                    ex.GetType().Name
                );
            }
        }

        private static bool ShouldProbeNestedMember(
            string memberName,
            Type declaredType,
            object value
        )
        {
            if (
                value == null ||
                value is string ||
                value is UnityEngine.Object ||
                value is Type ||
                declaredType == null ||
                declaredType.IsPrimitive ||
                declaredType.IsEnum
            )
            {
                return false;
            }

            string name =
                (
                    memberName ??
                    string.Empty
                ).ToLowerInvariant();

            string typeName =
                (
                    declaredType.FullName ??
                    declaredType.Name ??
                    string.Empty
                ).ToLowerInvariant();

            return
                name.Contains("material") ||
                name.Contains("texture") ||
                name.Contains("asset") ||
                name.Contains("render") ||
                name.Contains("mesh") ||
                name.Contains("geometry") ||
                name.Contains("shader") ||
                typeName.Contains("material") ||
                typeName.Contains("texture") ||
                typeName.Contains("asset") ||
                typeName.Contains("render") ||
                typeName.Contains("mesh") ||
                typeName.Contains("geometry") ||
                typeName.Contains("shader");
        }

        private static string FormatProbeValue(
            object value
        )
        {
            if (
                value == null
            )
            {
                return "null";
            }

            try
            {
                string text =
                    value.ToString();

                if (
                    string.IsNullOrEmpty(
                        text
                    )
                )
                {
                    return "<empty>";
                }

                text =
                    text.Replace(
                        "\r",
                        " "
                    ).Replace(
                        "\n",
                        " "
                    );

                if (
                    text.Length > 220
                )
                {
                    text =
                        text.Substring(
                            0,
                            220
                        ) +
                        "...";
                }

                return text;
            }
            catch
            {
                return "<ToString failed>";
            }
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

        private void RequestNativeRenderRefresh(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                visual.NativeRenderRefreshRequested
            )
            {
                return;
            }

            try
            {
                if (
                    !EntityManager.HasComponent<UnderConstruction>(
                        visual.Source
                    ) ||
                    !EntityManager.HasComponent<PrefabRef>(
                        visual.Source
                    )
                )
                {
                    return;
                }

                UnderConstruction construction =
                    EntityManager.GetComponentData<UnderConstruction>(
                        visual.Source
                    );

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        visual.Source
                    );

                int beforeCount =
                    EntityManager.HasBuffer<MeshBatch>(
                        visual.Source
                    )
                        ? EntityManager.GetBuffer<MeshBatch>(
                            visual.Source
                        ).Length
                        : -1;

                // V1.43.46.8:
                // Only construction-new entities use m_NewPrefab == Null.
                // A non-null value may be a legitimate vanilla transition
                // (upgrade/replacement), so never overwrite it.
                if (
                    construction.m_NewPrefab != Entity.Null
                )
                {
                    ModLog.Checkpoint(
                        "NATIVE-GATE SKIP; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; reason=newPrefabAlreadySet" +
                        "; progress=" +
                        construction.m_Progress +
                        "; currentNewPrefab=" +
                        construction.m_NewPrefab.Index +
                        ":" +
                        construction.m_NewPrefab.Version +
                        "; prefabRef=" +
                        prefabRef.m_Prefab.Index +
                        ":" +
                        prefabRef.m_Prefab.Version +
                        "; meshBatchCount=" +
                        beforeCount
                    );

                    return;
                }

                if (
                    prefabRef.m_Prefab == Entity.Null
                )
                {
                    ModLog.Checkpoint(
                        "NATIVE-GATE SKIP; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; reason=prefabRefNull" +
                        "; progress=" +
                        construction.m_Progress +
                        "; meshBatchCount=" +
                        beforeCount
                    );

                    return;
                }

                if (
                    beforeCount >
                    0
                )
                {
                    ModLog.Checkpoint(
                        "NATIVE-GATE SKIP; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; reason=meshBatchAlreadyPopulated" +
                        "; progress=" +
                        construction.m_Progress +
                        "; meshBatchCount=" +
                        beforeCount
                    );

                    return;
                }

                // Root-cause experiment:
                // BatchInstanceSystem.UpdateObjectInstances only populates
                // MeshBatch while UnderConstruction is present when
                // m_NewPrefab != Entity.Null. The mesh itself still comes
                // from PrefabRef.m_Prefab, so use the CURRENT prefab purely
                // to open the vanilla gate. Do not touch progress or speed.
                construction.m_NewPrefab =
                    prefabRef.m_Prefab;

                EntityManager.SetComponentData(
                    visual.Source,
                    construction
                );

                visual.NativeRenderGateInjected =
                    true;

                visual.NativeRenderGatePrefab =
                    prefabRef.m_Prefab;

                visual.NativeRenderRefreshRequested =
                    true;

                visual.NativeRenderRefreshRequestTime =
                    global::UnityEngine.Time.unscaledTime;

                visual.NextNativeRenderRefreshProbeTime =
                    visual.NativeRenderRefreshRequestTime +
                    0.10f;

                visual.NativeRenderRefreshProbeCount =
                    0;

                visual.NativeRenderRefreshSucceeded =
                    false;

                visual.NativeRenderGateRegressionLogged =
                    false;

                bool hadUpdated =
                    EntityManager.HasComponent<Updated>(
                        visual.Source
                    );

                if (
                    !hadUpdated
                )
                {
                    EntityManager.AddComponent<Updated>(
                        visual.Source
                    );
                }

                ModLog.Checkpoint(
                    "NATIVE-GATE OPEN; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; progress=" +
                    construction.m_Progress +
                    "; speed=" +
                    construction.m_Speed +
                    "; newPrefabBefore=null" +
                    "; injectedPrefab=" +
                    prefabRef.m_Prefab.Index +
                    ":" +
                    prefabRef.m_Prefab.Version +
                    "; meshBatchBefore=" +
                    beforeCount +
                    "; hadUpdated=" +
                    hadUpdated +
                    "; underConstruction=True"
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 native gate open failed; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; exception=" +
                    ex
                );
            }
        }

        private void ProbeNativeRenderRefresh(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                !visual.NativeRenderRefreshRequested ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                visual.NativeRenderRefreshProbeCount >=
                120
            )
            {
                return;
            }

            float now =
                global::UnityEngine.Time.unscaledTime;

            if (
                now <
                visual.NextNativeRenderRefreshProbeTime
            )
            {
                return;
            }

            // Probe quickly until the first success, then keep watching at a
            // lower frequency so camera/LOD/culling changes have time to
            // prove whether the native MeshBatch remains alive.
            visual.NextNativeRenderRefreshProbeTime =
                now +
                (
                    visual.NativeRenderRefreshSucceeded
                        ? 0.50f
                        : 0.25f
                );

            visual.NativeRenderRefreshProbeCount++;

            try
            {
                if (
                    !EntityManager.HasComponent<UnderConstruction>(
                        visual.Source
                    )
                )
                {
                    ModLog.Checkpoint(
                        "NATIVE-GATE PROBE STOP; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; reason=underConstructionRemoved" +
                        "; probe=" +
                        visual.NativeRenderRefreshProbeCount
                    );

                    visual.NativeRenderRefreshRequested =
                        false;

                    return;
                }

                UnderConstruction construction =
                    EntityManager.GetComponentData<UnderConstruction>(
                        visual.Source
                    );

                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        visual.Source
                    );

                int batchCount =
                    EntityManager.HasBuffer<MeshBatch>(
                        visual.Source
                    )
                        ? EntityManager.GetBuffer<MeshBatch>(
                            visual.Source
                        ).Length
                        : -1;

                int colorCount =
                    EntityManager.HasBuffer<MeshColor>(
                        visual.Source
                    )
                        ? EntityManager.GetBuffer<MeshColor>(
                            visual.Source
                        ).Length
                        : -1;

                bool gateStillOpen =
                    construction.m_NewPrefab != Entity.Null &&
                    construction.m_NewPrefab ==
                        visual.NativeRenderGatePrefab;

                ModLog.Checkpoint(
                    "NATIVE-GATE PROBE; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; probe=" +
                    visual.NativeRenderRefreshProbeCount +
                    "; progress=" +
                    construction.m_Progress +
                    "; meshBatchCount=" +
                    batchCount +
                    "; meshColorCount=" +
                    colorCount +
                    "; gateStillOpen=" +
                    gateStillOpen +
                    "; newPrefab=" +
                    construction.m_NewPrefab.Index +
                    ":" +
                    construction.m_NewPrefab.Version +
                    "; prefabRef=" +
                    prefabRef.m_Prefab.Index +
                    ":" +
                    prefabRef.m_Prefab.Version +
                    "; underConstruction=True"
                );

                if (
                    batchCount >
                    0 &&
                    gateStillOpen &&
                    !visual.NativeRenderRefreshSucceeded
                )
                {
                    visual.NativeRenderRefreshSucceeded =
                        true;

                    ModLog.Checkpoint(
                        "NATIVE-GATE SUCCESS; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; probe=" +
                        visual.NativeRenderRefreshProbeCount +
                        "; meshBatchCount=" +
                        batchCount +
                        "; meshColorCount=" +
                        colorCount +
                        "; underConstruction=True" +
                        "; newPrefabMatchesPrefabRef=" +
                        (
                            construction.m_NewPrefab ==
                            prefabRef.m_Prefab
                        ) +
                        "; BuildingVisualRootKept=True"
                    );

                    // V1.43.46.8 deliberately keeps the Unity clone visible.
                    // This version proves only that native ECS MeshBatch can
                    // remain populated. Visual hand-off is a separate test.
                }
                else if (
                    visual.NativeRenderRefreshSucceeded &&
                    batchCount <=
                    0 &&
                    !visual.NativeRenderGateRegressionLogged
                )
                {
                    visual.NativeRenderGateRegressionLogged =
                        true;

                    ModLog.Checkpoint(
                        "NATIVE-GATE REGRESSION; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; probe=" +
                        visual.NativeRenderRefreshProbeCount +
                        "; meshBatchCount=" +
                        batchCount +
                        "; gateStillOpen=" +
                        gateStillOpen +
                        "; underConstruction=True"
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 native gate probe failed; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; exception=" +
                    ex
                );
            }
        }

        private void CreateNativeProxy(
            ConstructionVisual visual,
            PrefabRef prefabRef
        )
        {
            // V1.43.37 SAFE-PROXY TEST:
            // Do not create an ECS building proxy. The previous proxy used
            // the real building PrefabRef plus Object/ObjectGeometry/Static,
            // which can be seen by game/editor systems as a real object.
            // Scaffolding remains active; only the building-rise proxy is
            // disabled so repositioning can be tested in isolation.
            visual.Proxy = Entity.Null;

            ModLog.Checkpoint(
                "PROXY disabled safe-test; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version
            );
        }

        private void SuspendConstructionVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Suspended
            )
            {
                return;
            }

            visual.Suspended =
                true;

            if (
                visual.ScaffoldRoot != null
            )
            {
                visual.ScaffoldRoot.SetActive(
                    false
                );
            }

            if (
                visual.CompanyBannerRoot != null
            )
            {
                visual.CompanyBannerRoot.SetActive(
                    false
                );
            }

            if (
                visual.BuildingVisualRoot != null
            )
            {
                visual.BuildingVisualRoot.SetActive(
                    false
                );
            }

            if (
                visual.Proxy != Entity.Null &&
                EntityManager.Exists(
                    visual.Proxy
                ) &&
                EntityManager.HasComponent<Game.Objects.Transform>(
                    visual.Proxy
                )
            )
            {
                Game.Objects.Transform proxyTransform =
                    EntityManager.GetComponentData<Game.Objects.Transform>(
                        visual.Proxy
                    );

                proxyTransform.m_Position.y -=
                    SuspendedProxyDepth;

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

            ModLog.Checkpoint(
                "VISUAL suspended; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; proxy=" +
                visual.Proxy.Index +
                ":" +
                visual.Proxy.Version +
                "; missingFrames=" +
                visual.MissingFrames
            );
        }

        private void ResumeConstructionVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                !visual.Suspended
            )
            {
                return;
            }

            visual.Suspended =
                false;

            if (
                visual.ScaffoldRoot != null
            )
            {
                visual.ScaffoldRoot.SetActive(
                    true
                );

                visual.ScaffoldDistanceVisible =
                    true;

                visual.NextScaffoldDistanceCheckTime =
                    0f;
            }

            if (
                visual.BuildingVisualRoot != null
            )
            {
                visual.BuildingVisualRoot.SetActive(
                    true
                );
            }

            ModLog.Checkpoint(
                "VISUAL resumed; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; proxy=" +
                visual.Proxy.Index +
                ":" +
                visual.Proxy.Version
            );
        }

        private void UpdateConstructionVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual ==
                null ||
                visual.Source ==
                Entity.Null
            )
            {
                return;
            }

            if (
                !EntityManager.Exists(
                    visual.Source
                )
            )
            {
                return;
            }

            // V1.43.37 SAFE-PROXY TEST:
            // The construction visual must continue updating even when the
            // ECS building proxy is intentionally disabled. Scaffold, crane
            // and progress logic are independent from visual.Proxy.

            if (
                !EntityManager.HasComponent<UnderConstruction>(
                    visual.Source
                )
            )
            {
                return;
            }

            ResumeConstructionVisual(
                visual
            );

            if (
                global::UnityEngine.Time.unscaledTime >=
                visual.NextConstructionSandAreaScanTime
            )
            {
                RetryConstructionSandSurfaceRemoval(
                    visual
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

            SetDiagnosticStage(
                "update.proxy",
                visual.Source
            );

            UpdateBuildingProxy(
                visual,
                sourceTransform,
                visual.VisualProgress
            );

            UpdateFoldedBuildingVisual(
                visual,
                sourceTransform,
                visual.VisualProgress
            );

            SetDiagnosticStage(
                "update.scaffold",
                visual.Source
            );

            UpdateScaffold(
                visual,
                sourceTransform,
                visual.VisualProgress
            );

            SetDiagnosticStage(
                "update.crane",
                visual.Source
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

        private void ApplyBuildingStateToFoldedBuilding(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                visual.BuildingVisualRoot == null
            )
            {
                return;
            }

            try
            {
                Entity stateEntity =
                    visual.Source;

                PseudoRandomSeed pseudoRandomSeed =
                    EntityManager.HasComponent<PseudoRandomSeed>(
                        stateEntity
                    )
                        ? EntityManager.GetComponentData<PseudoRandomSeed>(
                            stateEntity
                        )
                        : default(PseudoRandomSeed);

                if (
                    EntityManager.HasComponent<Owner>(
                        stateEntity
                    )
                )
                {
                    Owner owner =
                        EntityManager.GetComponentData<Owner>(
                            stateEntity
                        );

                    if (
                        owner.m_Owner != Entity.Null &&
                        EntityManager.Exists(
                            owner.m_Owner
                        )
                    )
                    {
                        stateEntity =
                            owner.m_Owner;
                    }
                }

                CitizenPresence citizenPresence =
                    EntityManager.HasComponent<CitizenPresence>(
                        stateEntity
                    )
                        ? EntityManager.GetComponentData<CitizenPresence>(
                            stateEntity
                        )
                        : default(CitizenPresence);

                bool abandoned =
                    EntityManager.HasComponent<Abandoned>(
                        stateEntity
                    ) ||
                    EntityManager.HasComponent<Destroyed>(
                        stateEntity
                    );

                bool electricity =
                    true;

                if (
                    EntityManager.HasComponent<Building>(
                        stateEntity
                    )
                )
                {
                    Building building =
                        EntityManager.GetComponentData<Building>(
                            stateEntity
                        );

                    electricity =
                        (
                            building.m_Flags &
                            Game.Buildings.BuildingFlags.Illuminated
                        ) != Game.Buildings.BuildingFlags.None;
                }

                float4 realBuildingState =
                    BatchDataHelpers.GetBuildingState(
                        pseudoRandomSeed,
                        citizenPresence,
                        1f,
                        abandoned,
                        electricity
                    );

                // V1.43.47.4.3.5 diagnostic: RenderPrefabRenderer.SetWindowProperties()
                // feeds BH/SG_WinShader with colossal_BuildingState=(0, randomWin, 0, 0).
                // Use a fixed mid-range value so this test changes only the window-state input.
                Vector4 unityBuildingState =
                    new Vector4(
                        0f,
                        0.5f,
                        0f,
                        0f
                    );

                MeshRenderer[] renderers =
                    visual.BuildingVisualRoot
                        .GetComponentsInChildren<MeshRenderer>(
                            true
                        );

                for (
                    int i = 0;
                    i < renderers.Length;
                    i++
                )
                {
                    MeshRenderer renderer =
                        renderers[i];

                    if (
                        renderer == null
                    )
                    {
                        continue;
                    }

                    MaterialPropertyBlock block =
                        new MaterialPropertyBlock();

                    renderer.GetPropertyBlock(
                        block
                    );

                    block.SetVector(
                        "colossal_BuildingState",
                        unityBuildingState
                    );

                    renderer.SetPropertyBlock(
                        block
                    );
                }

                ModLog.Checkpoint(
                    "CUTOFF-BUILDING-STATE debug-window; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; stateEntity=" +
                    stateEntity.Index +
                    ":" +
                    stateEntity.Version +
                    "; renderers=" +
                    renderers.Length +
                    "; value=" +
                    unityBuildingState +
                    "; realValue=" +
                    new Vector4(
                        realBuildingState.x,
                        realBuildingState.y,
                        realBuildingState.z,
                        realBuildingState.w
                    ) +
                    "; electricity=" +
                    electricity +
                    "; abandoned=" +
                    abandoned
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.47.4.3.5 building-state apply failed; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private void ApplySourceMeshColorToFoldedBuilding(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                visual.BuildingVisualRoot == null ||
                !EntityManager.HasBuffer<MeshColor>(
                    visual.Source
                )
            )
            {
                return;
            }

            DynamicBuffer<MeshColor> colors =
                EntityManager.GetBuffer<MeshColor>(
                    visual.Source
                );

            if (
                colors.Length <= 0
            )
            {
                return;
            }

            try
            {
                object boxedMeshColor =
                    colors[0];

                FieldInfo colorSetField =
                    boxedMeshColor
                        .GetType()
                        .GetField(
                            "m_ColorSet",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                object colorSet =
                    colorSetField != null
                        ? colorSetField.GetValue(
                            boxedMeshColor
                        )
                        : null;

                if (
                    colorSet == null
                )
                {
                    return;
                }

                Type colorSetType =
                    colorSet.GetType();

                FieldInfo channel0Field =
                    colorSetType.GetField(
                        "m_Channel0",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                FieldInfo channel1Field =
                    colorSetType.GetField(
                        "m_Channel1",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                FieldInfo channel2Field =
                    colorSetType.GetField(
                        "m_Channel2",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                if (
                    channel0Field == null ||
                    channel1Field == null ||
                    channel2Field == null
                )
                {
                    return;
                }

                UnityEngine.Color channel0 =
                    (UnityEngine.Color)
                    channel0Field.GetValue(
                        colorSet
                    );

                UnityEngine.Color channel1 =
                    (UnityEngine.Color)
                    channel1Field.GetValue(
                        colorSet
                    );

                UnityEngine.Color channel2 =
                    (UnityEngine.Color)
                    channel2Field.GetValue(
                        colorSet
                    );

                MeshRenderer[] renderers =
                    visual.BuildingVisualRoot
                        .GetComponentsInChildren<MeshRenderer>(
                            true
                        );

                for (
                    int i = 0;
                    i < renderers.Length;
                    i++
                )
                {
                    MeshRenderer renderer =
                        renderers[i];

                    if (
                        renderer == null
                    )
                    {
                        continue;
                    }

                    MaterialPropertyBlock block =
                        new MaterialPropertyBlock();

                    renderer.GetPropertyBlock(
                        block
                    );

                    // A single MeshRenderer can carry both the exterior shader
                    // and BH/SG_WinShader. Do not inspect only sharedMaterial[0]:
                    // the property block is renderer-wide and unused properties
                    // are harmless on materials that do not consume them.
                    block.SetColor(
                        "colossal_ColorMask0",
                        channel0
                    );

                    block.SetColor(
                        "colossal_ColorMask1",
                        channel1
                    );

                    block.SetColor(
                        "colossal_ColorMask2",
                        channel2
                    );

                    renderer.SetPropertyBlock(
                        block
                    );
                }

                ModLog.Checkpoint(
                    "CUTOFF-COLOR source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; renderers=" +
                    renderers.Length +
                    "; channel0=" +
                    channel0 +
                    "; channel1=" +
                    channel1 +
                    "; channel2=" +
                    channel2
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 BUILDING-COLOR apply failed; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private void ProbeLoadedSurfaceVT(
            SurfaceAsset surfaceAsset,
            Material loadedMaterial,
            Entity renderPrefabEntity,
            int surfaceIndex
        )
        {
            if (
                surfaceAsset == null ||
                loadedMaterial == null ||
                m_LoadedSurfaceVTProbeCount >=
                    MaxLoadedSurfaceVTProbes
            )
            {
                return;
            }

            m_LoadedSurfaceVTProbeCount++;

            try
            {
                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE begin; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; surface=" +
                    surfaceIndex +
                    "; surfaceType=" +
                    surfaceAsset.GetType().FullName +
                    "; materialId=" +
                    loadedMaterial.GetInstanceID() +
                    "; shader=" +
                    (
                        loadedMaterial.shader != null
                            ? loadedMaterial.shader.name
                            : "null"
                    )
                );

                Type surfaceType =
                    surfaceAsset.GetType();

                PropertyInfo atlassingProperty =
                    surfaceType.GetProperty(
                        "VTAtlassingInfos",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                object atlassingValue =
                    null;

                if (
                    atlassingProperty != null
                )
                {
                    try
                    {
                        atlassingValue =
                            atlassingProperty.GetValue(
                                surfaceAsset,
                                null
                            );
                    }
                    catch
                    {
                    }
                }

                Array atlassingArray =
                    atlassingValue as Array;

                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE atlassing; surface=" +
                    surfaceIndex +
                    "; propertyFound=" +
                    (atlassingProperty != null) +
                    "; valueType=" +
                    (
                        atlassingValue != null
                            ? atlassingValue.GetType().FullName
                            : "null"
                    ) +
                    "; count=" +
                    (
                        atlassingArray != null
                            ? atlassingArray.Length
                            : 0
                    )
                );

                if (
                    atlassingArray != null
                )
                {
                    int count =
                        Math.Min(
                            atlassingArray.Length,
                            8
                        );

                    for (
                        int i = 0;
                        i < count;
                        i++
                    )
                    {
                        object info =
                            atlassingArray.GetValue(
                                i
                            );

                        ProbeVTObject(
                            info,
                            "atlassing[" + i + "]",
                            64
                        );
                    }
                }

                FieldInfo vtSurfaceField =
                    surfaceType.GetField(
                        "m_VTSurfaceAsset",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                object vtSurface =
                    null;

                if (
                    vtSurfaceField != null
                )
                {
                    try
                    {
                        vtSurface =
                            vtSurfaceField.GetValue(
                                surfaceAsset
                            );
                    }
                    catch
                    {
                    }
                }

                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE vtSurface; surface=" +
                    surfaceIndex +
                    "; fieldFound=" +
                    (vtSurfaceField != null) +
                    "; valueType=" +
                    (
                        vtSurface != null
                            ? vtSurface.GetType().FullName
                            : "null"
                    )
                );

                if (
                    vtSurface != null
                )
                {
                    ProbeVTObject(
                        vtSurface,
                        "vtSurface",
                        64
                    );

                    FieldInfo instanceField =
                        vtSurface.GetType().GetField(
                            "m_Instance",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic
                        );

                    Material vtInstance =
                        null;

                    if (
                        instanceField != null
                    )
                    {
                        try
                        {
                            vtInstance =
                                instanceField.GetValue(
                                    vtSurface
                                ) as Material;
                        }
                        catch
                        {
                        }
                    }

                    ModLog.Checkpoint(
                        "BUILDING-VT-PROBE vtInstance; surface=" +
                        surfaceIndex +
                        "; material=" +
                        (
                            vtInstance != null
                                ? vtInstance.name
                                : "null"
                        ) +
                        "; materialId=" +
                        (
                            vtInstance != null
                                ? vtInstance.GetInstanceID()
                                : 0
                        ) +
                        "; shader=" +
                        (
                            vtInstance != null &&
                            vtInstance.shader != null
                                ? vtInstance.shader.name
                                : "null"
                        ) +
                        "; sameAsLoaded=" +
                        (
                            vtInstance != null &&
                            vtInstance == loadedMaterial
                        )
                    );

                    if (
                        vtInstance != null
                    )
                    {
                        ModLog.Checkpoint(
                            "BUILDING-VT-PROBE vtInstanceTextures; surface=" +
                            surfaceIndex +
                            "; textures=" +
                            CountMaterialTextures(
                                vtInstance
                            ) +
                            "; properties=" +
                            DescribeMaterialTextureProperties(
                                vtInstance
                            )
                        );
                    }
                }

                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE end; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; surface=" +
                    surfaceIndex
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 BUILDING-VT-PROBE failed; surface=" +
                    surfaceIndex +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private static void ProbeVTObject(
            object target,
            string label,
            int maxMembers
        )
        {
            if (
                target == null
            )
            {
                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE object; label=" +
                    label +
                    "; value=null"
                );

                return;
            }

            Type type =
                target.GetType();

            ModLog.Checkpoint(
                "BUILDING-VT-PROBE object; label=" +
                label +
                "; type=" +
                type.FullName +
                "; value=" +
                FormatProbeValue(
                    target
                )
            );

            int logged =
                0;

            Type current =
                type;

            while (
                current != null &&
                current != typeof(object) &&
                logged < maxMembers
            )
            {
                FieldInfo[] fields =
                    current.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                for (
                    int i = 0;
                    i < fields.Length &&
                    logged < maxMembers;
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
                                target
                            );
                    }
                    catch
                    {
                    }

                    ModLog.Checkpoint(
                        "BUILDING-VT-PROBE member; label=" +
                        label +
                        "; kind=field; name=" +
                        field.Name +
                        "; declared=" +
                        (
                            field.FieldType != null
                                ? field.FieldType.FullName
                                : "null"
                        ) +
                        "; valueType=" +
                        (
                            value != null
                                ? value.GetType().FullName
                                : "null"
                        ) +
                        "; value=" +
                        FormatProbeValue(
                            value
                        )
                    );

                    logged++;
                }

                current =
                    current.BaseType;
            }

            PropertyInfo[] properties =
                type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (
                int i = 0;
                i < properties.Length &&
                logged < maxMembers;
                i++
            )
            {
                PropertyInfo property =
                    properties[i];

                if (
                    property.GetIndexParameters().Length != 0
                )
                {
                    continue;
                }

                object value =
                    null;

                bool readable =
                    false;

                try
                {
                    value =
                        property.GetValue(
                            target,
                            null
                        );

                    readable =
                        true;
                }
                catch
                {
                }

                ModLog.Checkpoint(
                    "BUILDING-VT-PROBE member; label=" +
                    label +
                    "; kind=property; name=" +
                    property.Name +
                    "; declared=" +
                    (
                        property.PropertyType != null
                            ? property.PropertyType.FullName
                            : "null"
                    ) +
                    "; readable=" +
                    readable +
                    "; valueType=" +
                    (
                        value != null
                            ? value.GetType().FullName
                            : "null"
                    ) +
                    "; value=" +
                    FormatProbeValue(
                        value
                    )
                );

                logged++;
            }
        }

        private static void TryValidateHDMaterial(
            Material material,
            Entity renderPrefabEntity,
            int materialIndex
        )
        {
            if (material == null)
            {
                return;
            }

            try
            {
                Type hdMaterialType = Type.GetType(
                    "UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime",
                    false
                );

                if (hdMaterialType == null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        hdMaterialType = assembly.GetType(
                            "UnityEngine.Rendering.HighDefinition.HDMaterial",
                            false
                        );

                        if (hdMaterialType != null)
                        {
                            break;
                        }
                    }
                }

                MethodInfo validateMaterial =
                    hdMaterialType != null
                        ? hdMaterialType.GetMethod(
                            "ValidateMaterial",
                            BindingFlags.Static |
                            BindingFlags.Public |
                            BindingFlags.NonPublic,
                            null,
                            new Type[]
                            {
                                typeof(Material)
                            },
                            null
                        )
                        : null;

                if (validateMaterial != null)
                {
                    validateMaterial.Invoke(
                        null,
                        new object[]
                        {
                            material
                        }
                    );

                    ModLog.Info(
                        "CUTOFF-MATERIAL HDRP validate applied; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version +
                        "; materialIndex=" +
                        materialIndex
                    );
                }
                else
                {
                    ModLog.Info(
                        "CUTOFF-MATERIAL HDRP validate skipped; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version +
                        "; materialIndex=" +
                        materialIndex +
                        "; reason=HDMaterial.ValidateMaterial-not-found"
                    );
                }
            }
            catch (Exception validateEx)
            {
                ModLog.Info(
                    "CUTOFF-MATERIAL validate warning; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; materialIndex=" +
                    materialIndex +
                    "; error=" +
                    validateEx.GetType().Name
                );
            }
        }


        private void LogWindowInstancePropertyCatalog()
        {
            if (m_WindowInstancePropertyCatalogLogged)
            {
                return;
            }

            m_WindowInstancePropertyCatalogLogged =
                true;

            try
            {
                Type instancePropertyType =
                    Type.GetType(
                        "Game.Rendering.InstanceProperty, Game",
                        false
                    );

                if (
                    instancePropertyType == null ||
                    !instancePropertyType.IsEnum
                )
                {
                    ModLog.Checkpoint(
                        "WINDOW-INSTANCE-PROPERTY catalog unavailable; type=Game.Rendering.InstanceProperty"
                    );
                    return;
                }

                FieldInfo[] fields =
                    instancePropertyType.GetFields(
                        BindingFlags.Public |
                        BindingFlags.Static
                    );

                for (
                    int i = 0;
                    i < fields.Length;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

                    if (
                        field == null ||
                        field.Name == "value__"
                    )
                    {
                        continue;
                    }

                    string shaderPropertyName =
                        null;

                    string dataType =
                        null;

                    string isBuiltin =
                        null;

                    object[] attributes =
                        field.GetCustomAttributes(
                            false
                        );

                    for (
                        int attributeIndex = 0;
                        attributeIndex < attributes.Length;
                        attributeIndex++
                    )
                    {
                        object attribute =
                            attributes[
                                attributeIndex
                            ];

                        if (
                            attribute == null ||
                            attribute
                                .GetType()
                                .Name !=
                                "InstancePropertyAttribute"
                        )
                        {
                            continue;
                        }

                        Type attributeType =
                            attribute.GetType();

                        PropertyInfo shaderNameProperty =
                            attributeType.GetProperty(
                                "ShaderPropertyName",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic
                            );

                        PropertyInfo dataTypeProperty =
                            attributeType.GetProperty(
                                "DataType",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic
                            );

                        PropertyInfo builtinProperty =
                            attributeType.GetProperty(
                                "IsBuiltin",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic
                            );

                        object shaderNameValue =
                            shaderNameProperty != null
                                ? shaderNameProperty.GetValue(
                                    attribute,
                                    null
                                )
                                : null;

                        object dataTypeValue =
                            dataTypeProperty != null
                                ? dataTypeProperty.GetValue(
                                    attribute,
                                    null
                                )
                                : null;

                        object builtinValue =
                            builtinProperty != null
                                ? builtinProperty.GetValue(
                                    attribute,
                                    null
                                )
                                : null;

                        shaderPropertyName =
                            shaderNameValue != null
                                ? shaderNameValue.ToString()
                                : "null";

                        dataType =
                            dataTypeValue != null
                                ? dataTypeValue.ToString()
                                : "null";

                        isBuiltin =
                            builtinValue != null
                                ? builtinValue.ToString()
                                : "null";

                        break;
                    }

                    ModLog.Checkpoint(
                        "WINDOW-INSTANCE-PROPERTY; enum=" +
                        field.Name +
                        "; shaderProperty=" +
                        (
                            shaderPropertyName ??
                            "none"
                        ) +
                        "; dataType=" +
                        (
                            dataType ??
                            "none"
                        ) +
                        "; builtin=" +
                        (
                            isBuiltin ??
                            "none"
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "WINDOW-INSTANCE-PROPERTY catalog failed; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private static bool IsWindowDiagnosticPropertyName(
            string propertyName
        )
        {
            if (
                string.IsNullOrEmpty(
                    propertyName
                )
            )
            {
                return false;
            }

            string lower =
                propertyName.ToLowerInvariant();

            return
                lower.Contains(
                    "window"
                ) ||
                lower.Contains(
                    "interior"
                ) ||
                lower.Contains(
                    "mesh"
                ) ||
                lower.Contains(
                    "texture"
                ) ||
                lower.Contains(
                    "atlas"
                ) ||
                lower.Contains(
                    "random"
                ) ||
                lower.Contains(
                    "object"
                ) ||
                lower.Contains(
                    "color"
                ) ||
                lower.Contains(
                    "size"
                ) ||
                lower.Contains(
                    "area"
                ) ||
                lower.Contains(
                    "scale"
                ) ||
                lower.Contains(
                    "lod"
                ) ||
                lower.Contains(
                    "position"
                ) ||
                lower.Contains(
                    "bounds"
                ) ||
                lower.Contains(
                    "rotation"
                ) ||
                lower.Contains(
                    "index"
                );
        }

        private static string DescribeWindowMaterialState(
            Material material
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return "material=null";
            }

            List<string> values =
                new List<string>();

            Shader shader =
                material.shader;

            int propertyCount =
                shader.GetPropertyCount();

            for (
                int propertyIndex = 0;
                propertyIndex < propertyCount;
                propertyIndex++
            )
            {
                string propertyName =
                    shader.GetPropertyName(
                        propertyIndex
                    );

                if (
                    !IsWindowDiagnosticPropertyName(
                        propertyName
                    ) ||
                    !material.HasProperty(
                        propertyName
                    )
                )
                {
                    continue;
                }

                string propertyType =
                    shader
                        .GetPropertyType(
                            propertyIndex
                        )
                        .ToString();

                string value =
                    null;

                try
                {
                    if (
                        propertyType ==
                        "Texture"
                    )
                    {
                        Texture texture =
                            material.GetTexture(
                                propertyName
                            );

                        value =
                            texture != null
                                ? texture.name
                                : "null";
                    }
                    else if (
                        propertyType ==
                        "Color"
                    )
                    {
                        value =
                            material
                                .GetColor(
                                    propertyName
                                )
                                .ToString();
                    }
                    else if (
                        propertyType ==
                        "Vector"
                    )
                    {
                        value =
                            material
                                .GetVector(
                                    propertyName
                                )
                                .ToString();
                    }
                    else
                    {
                        value =
                            material
                                .GetFloat(
                                    propertyName
                                )
                                .ToString(
                                    "0.####"
                                );
                    }
                }
                catch
                {
                    value =
                        "read-failed";
                }

                values.Add(
                    propertyName +
                    "[" +
                    propertyType +
                    "]=" +
                    value
                );
            }

            string[] keywords =
                material.shaderKeywords;

            return
                "shader=" +
                shader.name +
                "; queue=" +
                material.renderQueue +
                "; instancing=" +
                material.enableInstancing +
                "; doubleSidedGI=" +
                material.doubleSidedGI +
                "; keywords=" +
                (
                    keywords != null &&
                    keywords.Length > 0
                        ? string.Join(
                            ",",
                            keywords
                        )
                        : "none"
                ) +
                "; values=" +
                (
                    values.Count > 0
                        ? string.Join(
                            "|",
                            values.ToArray()
                        )
                        : "none"
                );
        }

        private void LogWindowMaterialDiagnostics(
            Material sourceMaterial,
            Material displayMaterial,
            Entity renderPrefabEntity,
            int materialIndex
        )
        {
            if (
                sourceMaterial == null ||
                sourceMaterial.shader == null ||
                sourceMaterial.shader.name !=
                    "BH/SG_WinShader"
            )
            {
                return;
            }

            LogWindowInstancePropertyCatalog();

            ModLog.Checkpoint(
                "WINDOW-MATERIAL-DIAG source; renderPrefab=" +
                renderPrefabEntity.Index +
                ":" +
                renderPrefabEntity.Version +
                "; materialIndex=" +
                materialIndex +
                "; " +
                DescribeWindowMaterialState(
                    sourceMaterial
                )
            );

            ModLog.Checkpoint(
                "WINDOW-MATERIAL-DIAG clone; renderPrefab=" +
                renderPrefabEntity.Index +
                ":" +
                renderPrefabEntity.Version +
                "; materialIndex=" +
                materialIndex +
                "; " +
                DescribeWindowMaterialState(
                    displayMaterial
                )
            );
        }

        private Material CreateManagedBatchDisplayMaterial(
            RenderPrefab renderPrefab,
            SurfaceAsset surfaceAsset,
            Material sourceMaterial,
            int materialIndex,
            Entity renderPrefabEntity
        )
        {
            if (
                renderPrefab == null ||
                sourceMaterial == null
            )
            {
                return null;
            }

            Material displayMaterial =
                null;

            try
            {
                // V1.43.47.4.3.14: keep the fully loaded CS2 material intact.
                // Rebuilding from the SurfaceAsset template lost native shader state
                // and made the facade visibly worse. Clone the loaded material and
                // only ask ManagedBatchSystem.SetupVT to bind the VT/PVT stacks.
                displayMaterial =
                    new Material(
                        sourceMaterial
                    );

                displayMaterial.name =
                    "ConstructionAnimation_ManagedVT_" +
                    renderPrefabEntity.Index +
                    "_" +
                    renderPrefabEntity.Version +
                    "_" +
                    materialIndex;

                displayMaterial.hideFlags =
                    HideFlags.HideAndDontSave;

                ManagedBatchSystem managedBatchSystem =
                    World.GetOrCreateSystemManaged<
                        ManagedBatchSystem
                    >();

                if (
                    managedBatchSystem == null
                )
                {
                    ModLog.Checkpoint(
                        "CUTOFF-MATERIAL clone+VT; renderPrefab=" +
                        renderPrefabEntity.Index +
                        ":" +
                        renderPrefabEntity.Version +
                        "; materialIndex=" +
                        materialIndex +
                        "; vtBound=False; reason=ManagedBatchSystem-null"
                    );

                    return displayMaterial;
                }

                MethodInfo setupVT =
                    typeof(ManagedBatchSystem).GetMethod(
                        "SetupVT",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        new Type[]
                        {
                            typeof(RenderPrefab),
                            typeof(Material),
                            typeof(int)
                        },
                        null
                    );

                if (
                    setupVT != null
                )
                {
                    setupVT.Invoke(
                        managedBatchSystem,
                        new object[]
                        {
                            renderPrefab,
                            displayMaterial,
                            materialIndex
                        }
                    );
                }

                ConfigureScaffoldNoDecals(
                    displayMaterial,
                    "building-vt-" +
                    renderPrefabEntity.Index +
                    "-" +
                    materialIndex
                );

                // V1.43.47.4.3.14: the cutoff mesh exposes the inside of the
                // original one-sided building shell. The vanilla materials are
                // authored for exterior rendering, so back faces disappear when
                // the camera looks into the partially built structure. Disable
                // culling only on this cloned cutoff material.
                ConfigureCutoffDoubleSidedMaterial(
                    displayMaterial,
                    "building-cutoff-" +
                    renderPrefabEntity.Index +
                    "-" +
                    materialIndex
                );

                LogWindowMaterialDiagnostics(
                    sourceMaterial,
                    displayMaterial,
                    renderPrefabEntity,
                    materialIndex
                );

                bool hasDefaultAtlas0 =
                    displayMaterial.HasProperty(
                        "DefaultPVTStack_atlasParams0"
                    );

                bool hasDefaultAtlas1 =
                    displayMaterial.HasProperty(
                        "DefaultPVTStack_atlasParams1"
                    );

                bool hasExtendedAtlas0 =
                    displayMaterial.HasProperty(
                        "ExtendedPVTStack_atlasParams0"
                    );

                bool hasExtendedAtlas1 =
                    displayMaterial.HasProperty(
                        "ExtendedPVTStack_atlasParams1"
                    );

                ModLog.Checkpoint(
                    "CUTOFF-MATERIAL clone+VT; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; materialIndex=" +
                    materialIndex +
                    "; shader=" +
                    (
                        displayMaterial.shader != null
                            ? displayMaterial.shader.name
                            : "null"
                    ) +
                    "; vtBound=" +
                    (setupVT != null) +
                    "; atlasProps=" +
                    hasDefaultAtlas0 +
                    "," +
                    hasDefaultAtlas1 +
                    "," +
                    hasExtendedAtlas0 +
                    "," +
                    hasExtendedAtlas1 +
                    "; textures=" +
                    CountMaterialTextures(
                        displayMaterial
                    ) +
                    "; properties=" +
                    DescribeMaterialTextureProperties(
                        displayMaterial
                    )
                );

                return displayMaterial;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.47.4.3.5 clone+VT material failed; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; materialIndex=" +
                    materialIndex +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );

                return displayMaterial;
            }
        }

        private Material[] TryCreateFoldedMaterialsFromSurfaceAssets(
            ConstructionVisual visual,
            PrefabBase managedPrefab,
            Entity renderPrefabEntity,
            int requestedMaterialCount,
            int materialStartIndex
        )
        {
            if (
                visual == null ||
                managedPrefab == null
            )
            {
                return null;
            }

            RenderPrefab renderPrefab =
                managedPrefab as RenderPrefab;

            if (
                renderPrefab == null
            )
            {
                return null;
            }

            try
            {
                List<Material> result =
                    new List<Material>();

                int surfaceIndex =
                    0;

                foreach (
                    SurfaceAsset surfaceAsset in
                    renderPrefab.surfaceAssets
                )
                {
                    if (
                        surfaceAsset == null
                    )
                    {
                        surfaceIndex++;
                        continue;
                    }

                    Material loadedMaterial =
                        null;

                    try
                    {
                        // V1.43.37: load the real SurfaceAsset with VT enabled.
                        // The main CS2 building shaders keep their actual facade in the
                        // VT/PVT pipeline. Disabling VT exposes only conventional overlay
                        // textures such as snow, which is why the previous build rendered
                        // buildings as if snow were their base texture.
                        loadedMaterial =
                            surfaceAsset.Load(
                                -1,
                                true,
                                TextureAsset.KeepOnCPU.Dont,
                                true
                            );
                    }
                    catch (Exception ex)
                    {
                        ModLog.Info(
                            "V1.43.37 SurfaceAsset.Load failed; renderPrefab=" +
                            renderPrefabEntity.Index +
                            ":" +
                            renderPrefabEntity.Version +
                            "; surface=" +
                            surfaceIndex +
                            "; error=" +
                            ex.GetType().Name
                        );
                    }

                    if (
                        loadedMaterial != null
                    )
                    {

                        ProbeLoadedSurfaceVT(
                            surfaceAsset,
                            loadedMaterial,
                            renderPrefabEntity,
                            surfaceIndex
                        );
                        Material displayMaterial =
                            CreateManagedBatchDisplayMaterial(
                                renderPrefab,
                                surfaceAsset,
                                loadedMaterial,
                                surfaceIndex,
                                renderPrefabEntity
                            );

                        if (
                            displayMaterial == null
                        )
                        {
                            displayMaterial =
                                loadedMaterial;
                        }
                        else if (
                            displayMaterial != loadedMaterial
                        )
                        {
                            visual.BuildingVisualMaterials.Add(
                                displayMaterial
                            );
                        }

                        result.Add(
                            displayMaterial
                        );

                        visual.BuildingLoadedSurfaceAssets.Add(
                            surfaceAsset
                        );

                        ModLog.Checkpoint(
                            "BUILDING-FOLD display VT material; renderPrefab=" +
                            renderPrefabEntity.Index +
                            ":" +
                            renderPrefabEntity.Version +
                            "; surface=" +
                            surfaceIndex +
                            "; materialId=" +
                            displayMaterial.GetInstanceID() +
                            "; shader=" +
                            (
                                displayMaterial.shader != null
                                    ? displayMaterial.shader.name
                                    : "null"
                            ) +
                            "; textures=" +
                            CountMaterialTextures(
                                displayMaterial
                            ) +
                            "; properties=" +
                            DescribeMaterialTextureProperties(
                                displayMaterial
                            )
                        );
                    }

                    surfaceIndex++;
                }

                if (
                    result.Count == 0
                )
                {
                    return null;
                }

                int targetCount =
                    Mathf.Max(
                        1,
                        requestedMaterialCount
                    );

                Material[] materials =
                    new Material[
                        targetCount
                    ];

                for (
                    int i = 0;
                    i < materials.Length;
                    i++
                )
                {
                    materials[i] =
                        result[
                            Mathf.Min(
                                materialStartIndex + i,
                                result.Count - 1
                            )
                        ];
                }

                return materials;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 surface material creation failed; renderPrefab=" +
                    renderPrefabEntity.Index +
                    ":" +
                    renderPrefabEntity.Version +
                    "; error=" +
                    ex.GetType().Name
                );

                return null;
            }
        }

        private static int CountMaterialTextures(
            Material material
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return 0;
            }

            int count =
                0;

            try
            {
                int propertyCount =
                    material.shader.GetPropertyCount();

                for (
                    int i = 0;
                    i < propertyCount;
                    i++
                )
                {
                    if (
                        material.shader.GetPropertyType(
                            i
                        ) !=
                        UnityEngine.Rendering.ShaderPropertyType.Texture
                    )
                    {
                        continue;
                    }

                    int propertyId =
                        material.shader.GetPropertyNameId(
                            i
                        );

                    if (
                        material.GetTexture(
                            propertyId
                        ) != null
                    )
                    {
                        count++;
                    }
                }
            }
            catch
            {
            }

            return count;
        }

        private void ProbeSourceRenderingState(
            Entity source
        )
        {
            if (
                source == Entity.Null ||
                !EntityManager.Exists(source) ||
                m_SourceRenderingProbeEntities.Contains(
                    source.Index
                )
            )
            {
                return;
            }

            m_SourceRenderingProbeEntities.Add(
                source.Index
            );

            try
            {
                ModLog.Checkpoint(
                    "BUILDING-RENDER-STATE begin; source=" +
                    source.Index +
                    ":" +
                    source.Version
                );

                if (
                    EntityManager.HasBuffer<MeshBatch>(
                        source
                    )
                )
                {
                    DynamicBuffer<MeshBatch> batches =
                        EntityManager.GetBuffer<MeshBatch>(
                            source
                        );

                    ModLog.Checkpoint(
                        "BUILDING-RENDER-STATE meshBatchCount=" +
                        batches.Length
                    );

                    for (
                        int i = 0;
                        i < batches.Length;
                        i++
                    )
                    {
                        MeshBatch batch =
                            batches[i];

                        ModLog.Checkpoint(
                            "BUILDING-RENDER-STATE meshBatch; index=" +
                            i +
                            "; groupIndex=" +
                            batch.m_GroupIndex +
                            "; instanceIndex=" +
                            batch.m_InstanceIndex +
                            "; meshGroup=" +
                            batch.m_MeshGroup +
                            "; meshIndex=" +
                            batch.m_MeshIndex +
                            "; tileIndex=" +
                            batch.m_TileIndex
                        );
                    }
                }
                else
                {
                    ModLog.Checkpoint(
                        "BUILDING-RENDER-STATE meshBatchCount=0; bufferMissing=True"
                    );
                }

                if (
                    EntityManager.HasBuffer<MeshColor>(
                        source
                    )
                )
                {
                    DynamicBuffer<MeshColor> colors =
                        EntityManager.GetBuffer<MeshColor>(
                            source
                        );

                    ModLog.Checkpoint(
                        "BUILDING-RENDER-STATE meshColorCount=" +
                        colors.Length
                    );

                    for (
                        int i = 0;
                        i < colors.Length;
                        i++
                    )
                    {
                        object boxed =
                            colors[i];

                        ProbeVTObject(
                            boxed,
                            "source.meshColor[" + i + "]",
                            32
                        );
                    }
                }

                if (
                    EntityManager.HasComponent<Game.Objects.Surface>(
                        source
                    )
                )
                {
                    object boxedSurface =
                        EntityManager.GetComponentData<Game.Objects.Surface>(
                            source
                        );

                    ProbeVTObject(
                        boxedSurface,
                        "source.surface",
                        32
                    );
                }

                if (
                    EntityManager.HasComponent<Game.Objects.Color>(
                        source
                    )
                )
                {
                    object boxedColor =
                        EntityManager.GetComponentData<Game.Objects.Color>(
                            source
                        );

                    ProbeVTObject(
                        boxedColor,
                        "source.color",
                        32
                    );
                }

                ModLog.Checkpoint(
                    "BUILDING-RENDER-STATE end; source=" +
                    source.Index +
                    ":" +
                    source.Version
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 BUILDING-RENDER-STATE failed; source=" +
                    source.Index +
                    ":" +
                    source.Version +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private void ProbeManagedBatchSystemOnce()
        {
            if (
                m_ManagedBatchSystemProbed
            )
            {
                return;
            }

            m_ManagedBatchSystemProbed =
                true;

            try
            {
                ManagedBatchSystem system =
                    World.GetOrCreateSystemManaged<
                        ManagedBatchSystem
                    >();

                if (
                    system == null
                )
                {
                    ModLog.Checkpoint(
                        "MANAGED-BATCH-PROBE system=null"
                    );

                    return;
                }

                Type type =
                    system.GetType();

                ModLog.Checkpoint(
                    "MANAGED-BATCH-PROBE begin; type=" +
                    type.FullName
                );

                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                MethodInfo[] methods =
                    type.GetMethods(
                        flags
                    );

                int methodCount =
                    0;

                for (
                    int i = 0;
                    i < methods.Length &&
                    methodCount < 160;
                    i++
                )
                {
                    MethodInfo method =
                        methods[i];

                    string name =
                        method.Name;

                    string lower =
                        name.ToLowerInvariant();

                    if (
                        !lower.Contains("group") &&
                        !lower.Contains("material") &&
                        !lower.Contains("batch") &&
                        !lower.Contains("mesh") &&
                        !lower.Contains("vt")
                    )
                    {
                        continue;
                    }

                    ParameterInfo[] parameters =
                        method.GetParameters();

                    System.Text.StringBuilder signature =
                        new System.Text.StringBuilder();

                    for (
                        int p = 0;
                        p < parameters.Length;
                        p++
                    )
                    {
                        if (
                            p > 0
                        )
                        {
                            signature.Append(",");
                        }

                        signature.Append(
                            parameters[p].ParameterType.FullName
                        );

                        signature.Append(" ");

                        signature.Append(
                            parameters[p].Name
                        );
                    }

                    ModLog.Checkpoint(
                        "MANAGED-BATCH-PROBE method; name=" +
                        method.Name +
                        "; return=" +
                        (
                            method.ReturnType != null
                                ? method.ReturnType.FullName
                                : "null"
                        ) +
                        "; static=" +
                        method.IsStatic +
                        "; params=" +
                        signature.ToString()
                    );

                    methodCount++;
                }

                FieldInfo[] fields =
                    type.GetFields(
                        flags
                    );

                int fieldCount =
                    0;

                for (
                    int i = 0;
                    i < fields.Length &&
                    fieldCount < 120;
                    i++
                )
                {
                    FieldInfo field =
                        fields[i];

                    string lower =
                        field.Name.ToLowerInvariant();

                    string declaredLower =
                        field.FieldType != null
                            ? field.FieldType.FullName.ToLowerInvariant()
                            : string.Empty;

                    if (
                        !lower.Contains("group") &&
                        !lower.Contains("material") &&
                        !lower.Contains("batch") &&
                        !lower.Contains("mesh") &&
                        !lower.Contains("vt") &&
                        !declaredLower.Contains("group") &&
                        !declaredLower.Contains("material") &&
                        !declaredLower.Contains("batch") &&
                        !declaredLower.Contains("mesh") &&
                        !declaredLower.Contains("vt")
                    )
                    {
                        continue;
                    }

                    object value =
                        null;

                    try
                    {
                        value =
                            field.GetValue(
                                system
                            );
                    }
                    catch
                    {
                    }

                    ModLog.Checkpoint(
                        "MANAGED-BATCH-PROBE field; name=" +
                        field.Name +
                        "; declared=" +
                        (
                            field.FieldType != null
                                ? field.FieldType.FullName
                                : "null"
                        ) +
                        "; valueType=" +
                        (
                            value != null
                                ? value.GetType().FullName
                                : "null"
                        ) +
                        "; value=" +
                        FormatProbeValue(
                            value
                        )
                    );

                    fieldCount++;
                }

                PropertyInfo[] properties =
                    type.GetProperties(
                        flags
                    );

                int propertyCount =
                    0;

                for (
                    int i = 0;
                    i < properties.Length &&
                    propertyCount < 80;
                    i++
                )
                {
                    PropertyInfo property =
                        properties[i];

                    string lower =
                        property.Name.ToLowerInvariant();

                    string declaredLower =
                        property.PropertyType != null
                            ? property.PropertyType.FullName.ToLowerInvariant()
                            : string.Empty;

                    if (
                        !lower.Contains("group") &&
                        !lower.Contains("material") &&
                        !lower.Contains("batch") &&
                        !lower.Contains("mesh") &&
                        !lower.Contains("vt") &&
                        !declaredLower.Contains("group") &&
                        !declaredLower.Contains("material") &&
                        !declaredLower.Contains("batch") &&
                        !declaredLower.Contains("mesh") &&
                        !declaredLower.Contains("vt")
                    )
                    {
                        continue;
                    }

                    ModLog.Checkpoint(
                        "MANAGED-BATCH-PROBE property; name=" +
                        property.Name +
                        "; declared=" +
                        (
                            property.PropertyType != null
                                ? property.PropertyType.FullName
                                : "null"
                        ) +
                        "; canRead=" +
                        property.CanRead
                    );

                    propertyCount++;
                }

                ModLog.Checkpoint(
                    "MANAGED-BATCH-PROBE end; methods=" +
                    methodCount +
                    "; fields=" +
                    fieldCount +
                    "; properties=" +
                    propertyCount
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 MANAGED-BATCH-PROBE failed; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private void BuildVolumetricFloorProfiles(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            visual.FloorFootprints.Clear();

            if (
                visual.FloorBoundaries == null ||
                visual.FloorBoundaries.Count < 2 ||
                visual.StructureTriangleVertices == null ||
                visual.StructureTriangleVertices.Count < 3
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
                i < visual.StructureTriangleVertices.Count;
                i++
            )
            {
                Vector3 vertex =
                    visual.StructureTriangleVertices[i];

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
                        vertex.z
                    );

                maxZ =
                    Mathf.Max(
                        maxZ,
                        vertex.z
                    );
            }

            float widthMeters =
                Mathf.Max(
                    1f,
                    maxX -
                    minX
                );

            float depthMeters =
                Mathf.Max(
                    1f,
                    maxZ -
                    minZ
                );

            float maxDimension =
                Mathf.Max(
                    widthMeters,
                    depthMeters
                );

            float cellSize =
                Mathf.Clamp(
                    maxDimension /
                    72f,
                    0.35f,
                    0.75f
                );

            float margin =
                cellSize *
                2.5f;

            minX -=
                margin;

            minZ -=
                margin;

            int gridWidth =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        (
                            widthMeters +
                            margin *
                            2f
                        ) /
                        cellSize
                    ),
                    8,
                    96
                );

            int gridHeight =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        (
                            depthMeters +
                            margin *
                            2f
                        ) /
                        cellSize
                    ),
                    8,
                    96
                );

            List<Vector2> previousAccepted =
                visual.Footprint != null &&
                visual.Footprint.Count >= 3
                    ? SimplifySliceLoop(
                        new List<Vector2>(
                            visual.Footprint
                        )
                    )
                    : new List<Vector2>();

            float previousArea =
                Mathf.Max(
                    0.01f,
                    Mathf.Abs(
                        SignedPolygonArea(
                            previousAccepted
                        )
                    )
                );

            int floorCount =
                visual.FloorBoundaries.Count -
                1;

            for (
                int floorIndex = 0;
                floorIndex < floorCount;
                floorIndex++
            )
            {
                float bottomLocalY =
                    visual.FloorBoundaries[
                        floorIndex
                    ];

                float topLocalY =
                    visual.FloorBoundaries[
                        floorIndex +
                        1
                    ];

                float bandBottomY =
                    visual.StructureGeometryBaseY +
                    bottomLocalY +
                    0.04f;

                float bandTopY =
                    visual.StructureGeometryBaseY +
                    topLocalY -
                    0.04f;

                FloorRasterProfile barrierMask =
                    new FloorRasterProfile
                    {
                        MinX =
                            minX,
                        MinZ =
                            minZ,
                        CellSize =
                            cellSize,
                        Width =
                            gridWidth,
                        Height =
                            gridHeight,
                        OccupiedCells =
                            new bool[
                                gridWidth *
                                gridHeight
                            ],
                        OccupiedCount =
                            0
                    };

                int overlappingTriangles =
                    0;

                for (
                    int triangleIndex = 0;
                    triangleIndex + 2 < visual.StructureTriangleVertices.Count;
                    triangleIndex += 3
                )
                {
                    Vector3 a =
                        visual.StructureTriangleVertices[
                            triangleIndex
                        ];

                    Vector3 b =
                        visual.StructureTriangleVertices[
                            triangleIndex +
                            1
                        ];

                    Vector3 c =
                        visual.StructureTriangleVertices[
                            triangleIndex +
                            2
                        ];

                    float triangleMinY =
                        Mathf.Min(
                            a.y,
                            Mathf.Min(
                                b.y,
                                c.y
                            )
                        );

                    float triangleMaxY =
                        Mathf.Max(
                            a.y,
                            Mathf.Max(
                                b.y,
                                c.y
                            )
                        );

                    if (
                        triangleMaxY <
                            bandBottomY ||
                        triangleMinY >
                            bandTopY
                    )
                    {
                        continue;
                    }

                    overlappingTriangles++;

                    MarkProjectedTriangleOnMask(
                        barrierMask,
                        new Vector2(
                            a.x,
                            a.z
                        ),
                        new Vector2(
                            b.x,
                            b.z
                        ),
                        new Vector2(
                            c.x,
                            c.z
                        )
                    );
                }

                SealProjectedBarriers(
                    barrierMask
                );

                FloorRasterProfile filledMask =
                    FillProjectedVolumeInterior(
                        barrierMask
                    );

                KeepLargestRasterComponent(
                    filledMask
                );

                List<SliceSegment> boundarySegments =
                    BuildRasterBoundarySegments(
                        filledMask
                    );

                List<List<Vector2>> loops =
                    BuildSliceLoops(
                        boundarySegments
                    );

                List<Vector2> candidate =
                    SelectLargestSliceLoop(
                        loops
                    );

                string decision =
                    "reuse-previous";

                if (
                    candidate != null &&
                    candidate.Count >= 3
                )
                {
                    candidate =
                        SimplifySliceLoop(
                            candidate
                        );

                    if (
                        SignedPolygonArea(
                            candidate
                        ) < 0f
                    )
                    {
                        candidate.Reverse();
                    }

                    float candidateArea =
                        Mathf.Abs(
                            SignedPolygonArea(
                                candidate
                            )
                        );

                    float ratio =
                        candidateArea /
                        Mathf.Max(
                            0.01f,
                            previousArea
                        );

                    bool plausible =
                        candidateArea >=
                            Mathf.Max(
                                4f,
                                cellSize *
                                cellSize *
                                8f
                            ) &&
                        ratio >=
                            0.18f &&
                        ratio <=
                            1.35f &&
                        IsSimplePolygon2D(
                            candidate
                        );

                    if (
                        plausible
                    )
                    {
                        previousAccepted =
                            candidate;

                        previousArea =
                            candidateArea;

                        decision =
                            "accept-volume";
                    }
                    else
                    {
                        decision =
                            "reject-volume";
                    }
                }

                visual.FloorFootprints.Add(
                    new List<Vector2>(
                        previousAccepted
                    )
                );

                ModLog.Checkpoint(
                    "STRUCTURE-VOLUME-PROFILE; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; floor=" +
                    floorIndex +
                    "; band=" +
                    bottomLocalY.ToString(
                        "0.00"
                    ) +
                    "-" +
                    topLocalY.ToString(
                        "0.00"
                    ) +
                    "; triangles=" +
                    overlappingTriangles +
                    "; grid=" +
                    gridWidth +
                    "x" +
                    gridHeight +
                    "; cell=" +
                    cellSize.ToString(
                        "0.00"
                    ) +
                    "; occupied=" +
                    (
                        filledMask != null
                            ? filledMask.OccupiedCount
                            : 0
                    ) +
                    "; loops=" +
                    loops.Count +
                    "; points=" +
                    previousAccepted.Count +
                    "; area=" +
                    previousArea.ToString(
                        "0.00"
                    ) +
                    "; decision=" +
                    decision
                );
            }
        }

        private static void MarkProjectedTriangleOnMask(
            FloorRasterProfile mask,
            Vector2 a,
            Vector2 b,
            Vector2 c
        )
        {
            if (
                mask == null ||
                mask.OccupiedCells == null
            )
            {
                return;
            }

            float projectedArea =
                Mathf.Abs(
                    Cross2D(
                        b -
                        a,
                        c -
                        a
                    )
                ) *
                0.5f;

            RasterizeProjectedLine(
                mask,
                a,
                b
            );

            RasterizeProjectedLine(
                mask,
                b,
                c
            );

            RasterizeProjectedLine(
                mask,
                c,
                a
            );

            if (
                projectedArea <
                mask.CellSize *
                mask.CellSize *
                0.10f
            )
            {
                return;
            }

            float minTriangleX =
                Mathf.Min(
                    a.x,
                    Mathf.Min(
                        b.x,
                        c.x
                    )
                );

            float maxTriangleX =
                Mathf.Max(
                    a.x,
                    Mathf.Max(
                        b.x,
                        c.x
                    )
                );

            float minTriangleZ =
                Mathf.Min(
                    a.y,
                    Mathf.Min(
                        b.y,
                        c.y
                    )
                );

            float maxTriangleZ =
                Mathf.Max(
                    a.y,
                    Mathf.Max(
                        b.y,
                        c.y
                    )
                );

            int minCellX =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        (
                            minTriangleX -
                            mask.MinX
                        ) /
                        mask.CellSize
                    ),
                    0,
                    mask.Width -
                    1
                );

            int maxCellX =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        (
                            maxTriangleX -
                            mask.MinX
                        ) /
                        mask.CellSize
                    ),
                    0,
                    mask.Width -
                    1
                );

            int minCellZ =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        (
                            minTriangleZ -
                            mask.MinZ
                        ) /
                        mask.CellSize
                    ),
                    0,
                    mask.Height -
                    1
                );

            int maxCellZ =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        (
                            maxTriangleZ -
                            mask.MinZ
                        ) /
                        mask.CellSize
                    ),
                    0,
                    mask.Height -
                    1
                );

            for (
                int z = minCellZ;
                z <= maxCellZ;
                z++
            )
            {
                for (
                    int x = minCellX;
                    x <= maxCellX;
                    x++
                )
                {
                    Vector2 point =
                        new Vector2(
                            mask.MinX +
                            (
                                x +
                                0.5f
                            ) *
                            mask.CellSize,
                            mask.MinZ +
                            (
                                z +
                                0.5f
                            ) *
                            mask.CellSize
                        );

                    if (
                        PointInTriangle2D(
                            point,
                            a,
                            b,
                            c
                        )
                    )
                    {
                        SetRasterCell(
                            mask,
                            x,
                            z,
                            true
                        );
                    }
                }
            }
        }

        private static void RasterizeProjectedLine(
            FloorRasterProfile mask,
            Vector2 a,
            Vector2 b
        )
        {
            if (
                mask == null ||
                mask.OccupiedCells == null
            )
            {
                return;
            }

            float length =
                Vector2.Distance(
                    a,
                    b
                );

            int steps =
                Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        length /
                        Mathf.Max(
                            0.05f,
                            mask.CellSize *
                            0.35f
                        )
                    )
                );

            for (
                int step = 0;
                step <= steps;
                step++
            )
            {
                Vector2 point =
                    Vector2.Lerp(
                        a,
                        b,
                        step /
                        (float)steps
                    );

                int x =
                    Mathf.FloorToInt(
                        (
                            point.x -
                            mask.MinX
                        ) /
                        mask.CellSize
                    );

                int z =
                    Mathf.FloorToInt(
                        (
                            point.y -
                            mask.MinZ
                        ) /
                        mask.CellSize
                    );

                SetRasterCell(
                    mask,
                    x,
                    z,
                    true
                );
            }
        }

        private static void SetRasterCell(
            FloorRasterProfile mask,
            int x,
            int z,
            bool value
        )
        {
            if (
                mask == null ||
                mask.OccupiedCells == null ||
                x < 0 ||
                z < 0 ||
                x >= mask.Width ||
                z >= mask.Height
            )
            {
                return;
            }

            int index =
                z *
                mask.Width +
                x;

            bool previous =
                mask.OccupiedCells[
                    index
                ];

            if (
                previous ==
                value
            )
            {
                return;
            }

            mask.OccupiedCells[
                index
            ] =
                value;

            mask.OccupiedCount +=
                value
                    ? 1
                    : -1;
        }

        private static void SealProjectedBarriers(
            FloorRasterProfile mask
        )
        {
            if (
                mask == null ||
                mask.OccupiedCells == null
            )
            {
                return;
            }

            bool[] source =
                (
                    bool[]
                )mask.OccupiedCells.Clone();

            for (
                int z = 0;
                z < mask.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < mask.Width;
                    x++
                )
                {
                    int index =
                        z *
                        mask.Width +
                        x;

                    if (
                        !source[index]
                    )
                    {
                        continue;
                    }

                    for (
                        int dz = -1;
                        dz <= 1;
                        dz++
                    )
                    {
                        for (
                            int dx = -1;
                            dx <= 1;
                            dx++
                        )
                        {
                            if (
                                Mathf.Abs(
                                    dx
                                ) +
                                Mathf.Abs(
                                    dz
                                ) >
                                1
                            )
                            {
                                continue;
                            }

                            SetRasterCell(
                                mask,
                                x +
                                dx,
                                z +
                                dz,
                                true
                            );
                        }
                    }
                }
            }
        }

        private static FloorRasterProfile FillProjectedVolumeInterior(
            FloorRasterProfile barrierMask
        )
        {
            if (
                barrierMask == null ||
                barrierMask.OccupiedCells == null
            )
            {
                return null;
            }

            bool[] outside =
                new bool[
                    barrierMask.OccupiedCells.Length
                ];

            List<int> queue =
                new List<int>();

            int queueHead =
                0;

            for (
                int x = 0;
                x < barrierMask.Width;
                x++
            )
            {
                TryQueueOutsideCell(
                    barrierMask,
                    outside,
                    queue,
                    x,
                    0
                );

                TryQueueOutsideCell(
                    barrierMask,
                    outside,
                    queue,
                    x,
                    barrierMask.Height -
                    1
                );
            }

            for (
                int z = 0;
                z < barrierMask.Height;
                z++
            )
            {
                TryQueueOutsideCell(
                    barrierMask,
                    outside,
                    queue,
                    0,
                    z
                );

                TryQueueOutsideCell(
                    barrierMask,
                    outside,
                    queue,
                    barrierMask.Width -
                    1,
                    z
                );
            }

            int[] dx =
                new int[]
                {
                    1,
                    -1,
                    0,
                    0
                };

            int[] dz =
                new int[]
                {
                    0,
                    0,
                    1,
                    -1
                };

            while (
                queueHead <
                queue.Count
            )
            {
                int current =
                    queue[
                        queueHead
                    ];

                queueHead++;

                int currentX =
                    current %
                    barrierMask.Width;

                int currentZ =
                    current /
                    barrierMask.Width;

                for (
                    int direction = 0;
                    direction < 4;
                    direction++
                )
                {
                    TryQueueOutsideCell(
                        barrierMask,
                        outside,
                        queue,
                        currentX +
                        dx[direction],
                        currentZ +
                        dz[direction]
                    );
                }
            }

            FloorRasterProfile filled =
                new FloorRasterProfile
                {
                    MinX =
                        barrierMask.MinX,
                    MinZ =
                        barrierMask.MinZ,
                    CellSize =
                        barrierMask.CellSize,
                    Width =
                        barrierMask.Width,
                    Height =
                        barrierMask.Height,
                    OccupiedCells =
                        new bool[
                            barrierMask.OccupiedCells.Length
                        ],
                    OccupiedCount =
                        0
                };

            for (
                int i = 0;
                i < filled.OccupiedCells.Length;
                i++
            )
            {
                bool occupied =
                    barrierMask.OccupiedCells[i] ||
                    !outside[i];

                filled.OccupiedCells[i] =
                    occupied;

                if (
                    occupied
                )
                {
                    filled.OccupiedCount++;
                }
            }

            return filled;
        }

        private static void TryQueueOutsideCell(
            FloorRasterProfile barrierMask,
            bool[] outside,
            List<int> queue,
            int x,
            int z
        )
        {
            if (
                barrierMask == null ||
                outside == null ||
                queue == null ||
                x < 0 ||
                z < 0 ||
                x >= barrierMask.Width ||
                z >= barrierMask.Height
            )
            {
                return;
            }

            int index =
                z *
                barrierMask.Width +
                x;

            if (
                outside[index] ||
                barrierMask.OccupiedCells[index]
            )
            {
                return;
            }

            outside[index] =
                true;

            queue.Add(
                index
            );
        }

        private static List<SliceSegment> BuildRasterBoundarySegments(
            FloorRasterProfile profile
        )
        {
            List<SliceSegment> result =
                new List<SliceSegment>();

            if (
                profile == null ||
                profile.OccupiedCells == null
            )
            {
                return result;
            }

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    if (
                        !profile.IsOccupied(
                            x,
                            z
                        )
                    )
                    {
                        continue;
                    }

                    float x0 =
                        profile.MinX +
                        x *
                        profile.CellSize;

                    float x1 =
                        x0 +
                        profile.CellSize;

                    float z0 =
                        profile.MinZ +
                        z *
                        profile.CellSize;

                    float z1 =
                        z0 +
                        profile.CellSize;

                    if (
                        !profile.IsOccupied(
                            x,
                            z -
                            1
                        )
                    )
                    {
                        result.Add(
                            new SliceSegment
                            {
                                A =
                                    new Vector2(
                                        x0,
                                        z0
                                    ),
                                B =
                                    new Vector2(
                                        x1,
                                        z0
                                    )
                            }
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x +
                            1,
                            z
                        )
                    )
                    {
                        result.Add(
                            new SliceSegment
                            {
                                A =
                                    new Vector2(
                                        x1,
                                        z0
                                    ),
                                B =
                                    new Vector2(
                                        x1,
                                        z1
                                    )
                            }
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x,
                            z +
                            1
                        )
                    )
                    {
                        result.Add(
                            new SliceSegment
                            {
                                A =
                                    new Vector2(
                                        x1,
                                        z1
                                    ),
                                B =
                                    new Vector2(
                                        x0,
                                        z1
                                    )
                            }
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x -
                            1,
                            z
                        )
                    )
                    {
                        result.Add(
                            new SliceSegment
                            {
                                A =
                                    new Vector2(
                                        x0,
                                        z1
                                    ),
                                B =
                                    new Vector2(
                                        x0,
                                        z0
                                    )
                            }
                        );
                    }
                }
            }

            return result;
        }

        private void BuildExactPrincipalMeshFloorProfiles(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            visual.FloorFootprints.Clear();

            if (
                visual.FloorBoundaries == null ||
                visual.FloorBoundaries.Count < 2 ||
                visual.StructureTriangleVertices == null ||
                visual.StructureTriangleVertices.Count < 3
            )
            {
                return;
            }

            int floorCount =
                visual.FloorBoundaries.Count -
                1;

            List<Vector2> previousAccepted =
                visual.Footprint != null
                    ? SimplifySliceLoop(
                        new List<Vector2>(
                            visual.Footprint
                        )
                    )
                    : new List<Vector2>();

            float previousArea =
                Mathf.Max(
                    0.01f,
                    Mathf.Abs(
                        SignedPolygonArea(
                            previousAccepted
                        )
                    )
                );

            for (
                int floorIndex = 0;
                floorIndex < floorCount;
                floorIndex++
            )
            {
                float bottom =
                    visual.FloorBoundaries[
                        floorIndex
                    ];

                float top =
                    visual.FloorBoundaries[
                        floorIndex +
                        1
                    ];

                float sampleLocalY =
                    Mathf.Lerp(
                        bottom,
                        top,
                        0.50f
                    );

                float planeY =
                    visual.StructureGeometryBaseY +
                    sampleLocalY;

                List<SliceSegment> rawSegments =
                    new List<SliceSegment>();

                for (
                    int triangleIndex = 0;
                    triangleIndex + 2 < visual.StructureTriangleVertices.Count;
                    triangleIndex += 3
                )
                {
                    AddTriangleSliceSegments(
                        visual.StructureTriangleVertices[triangleIndex],
                        visual.StructureTriangleVertices[triangleIndex + 1],
                        visual.StructureTriangleVertices[triangleIndex + 2],
                        planeY,
                        rawSegments
                    );
                }

                List<SliceSegment> cleanedSegments =
                    CleanPrincipalSliceSegments(
                        rawSegments
                    );

                List<List<Vector2>> loops =
                    BuildSliceLoops(
                        cleanedSegments
                    );

                List<Vector2> candidate =
                    SelectBestPrincipalSliceLoop(
                        loops,
                        previousArea
                    );

                string decision =
                    "reuse-previous";

                if (
                    candidate != null &&
                    candidate.Count >= 3
                )
                {
                    candidate =
                        SimplifySliceLoop(
                            candidate
                        );

                    if (
                        SignedPolygonArea(
                            candidate
                        ) < 0f
                    )
                    {
                        candidate.Reverse();
                    }

                    float candidateArea =
                        Mathf.Abs(
                            SignedPolygonArea(
                                candidate
                            )
                        );

                    float ratio =
                        candidateArea /
                        Mathf.Max(
                            0.01f,
                            previousArea
                        );

                    bool plausible =
                        candidate.Count >= 3 &&
                        IsSimplePolygon2D(
                            candidate
                        ) &&
                        candidateArea >= 2f &&
                        ratio >= 0.12f &&
                        ratio <= 1.35f;

                    if (
                        plausible
                    )
                    {
                        previousAccepted =
                            candidate;

                        previousArea =
                            candidateArea;

                        decision =
                            "accept-principal";
                    }
                    else
                    {
                        decision =
                            "reject-principal";
                    }
                }

                visual.FloorFootprints.Add(
                    new List<Vector2>(
                        previousAccepted
                    )
                );

                ModLog.Checkpoint(
                    "STRUCTURE-PRINCIPAL-PROFILE; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; floor=" +
                    floorIndex +
                    "; y=" +
                    sampleLocalY.ToString(
                        "0.00"
                    ) +
                    "; rawSegments=" +
                    rawSegments.Count +
                    "; cleanedSegments=" +
                    cleanedSegments.Count +
                    "; loops=" +
                    loops.Count +
                    "; points=" +
                    previousAccepted.Count +
                    "; area=" +
                    previousArea.ToString(
                        "0.00"
                    ) +
                    "; decision=" +
                    decision
                );
            }
        }

        private static List<SliceSegment> CleanPrincipalSliceSegments(
            List<SliceSegment> input
        )
        {
            List<SliceSegment> result =
                new List<SliceSegment>();

            if (
                input == null ||
                input.Count == 0
            )
            {
                return result;
            }

            const float weld =
                0.03f;

            Dictionary<string, SliceSegment> unique =
                new Dictionary<string, SliceSegment>();

            for (
                int i = 0;
                i < input.Count;
                i++
            )
            {
                Vector2 a =
                    input[i].A;

                Vector2 b =
                    input[i].B;

                if (
                    (
                        a -
                        b
                    ).sqrMagnitude <
                    0.0004f
                )
                {
                    continue;
                }

                Vector2 wa =
                    new Vector2(
                        Mathf.Round(
                            a.x /
                            weld
                        ) *
                        weld,
                        Mathf.Round(
                            a.y /
                            weld
                        ) *
                        weld
                    );

                Vector2 wb =
                    new Vector2(
                        Mathf.Round(
                            b.x /
                            weld
                        ) *
                        weld,
                        Mathf.Round(
                            b.y /
                            weld
                        ) *
                        weld
                    );

                string keyA =
                    SlicePointKey(
                        wa
                    );

                string keyB =
                    SlicePointKey(
                        wb
                    );

                string key =
                    string.CompareOrdinal(
                        keyA,
                        keyB
                    ) <= 0
                        ? keyA +
                          "|" +
                          keyB
                        : keyB +
                          "|" +
                          keyA;

                if (
                    !unique.ContainsKey(
                        key
                    )
                )
                {
                    unique[
                        key
                    ] =
                        new SliceSegment
                        {
                            A =
                                wa,
                            B =
                                wb
                        };
                }
            }

            foreach (
                KeyValuePair<string, SliceSegment> pair
                in unique
            )
            {
                result.Add(
                    pair.Value
                );
            }

            return result;
        }

        private static List<Vector2> SelectBestPrincipalSliceLoop(
            List<List<Vector2>> loops,
            float previousArea
        )
        {
            if (
                loops == null ||
                loops.Count == 0
            )
            {
                return null;
            }

            List<Vector2> best =
                null;

            float bestScore =
                float.MinValue;

            for (
                int i = 0;
                i < loops.Count;
                i++
            )
            {
                List<Vector2> loop =
                    loops[i];

                if (
                    loop == null ||
                    loop.Count < 3
                )
                {
                    continue;
                }

                List<Vector2> simplified =
                    SimplifySliceLoop(
                        loop
                    );

                if (
                    simplified.Count < 3 ||
                    !IsSimplePolygon2D(
                        simplified
                    )
                )
                {
                    continue;
                }

                float area =
                    Mathf.Abs(
                        SignedPolygonArea(
                            simplified
                        )
                    );

                if (
                    area <
                    2f
                )
                {
                    continue;
                }

                float continuity =
                    Mathf.Min(
                        area,
                        previousArea
                    ) /
                    Mathf.Max(
                        Mathf.Max(
                            area,
                            previousArea
                        ),
                        0.01f
                    );

                float score =
                    area *
                    Mathf.Lerp(
                        0.45f,
                        1f,
                        continuity
                    );

                if (
                    score >
                    bestScore
                )
                {
                    bestScore =
                        score;

                    best =
                        simplified;
                }
            }

            return best != null
                ? new List<Vector2>(
                    best
                )
                : null;
        }

        private static bool IsSimplePolygon2D(
            List<Vector2> polygon
        )
        {
            if (
                polygon == null ||
                polygon.Count < 3
            )
            {
                return false;
            }

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 a1 =
                    polygon[i];

                Vector2 a2 =
                    polygon[
                        (
                            i +
                            1
                        ) %
                        polygon.Count
                    ];

                for (
                    int j = i + 1;
                    j < polygon.Count;
                    j++
                )
                {
                    int nextJ =
                        (
                            j +
                            1
                        ) %
                        polygon.Count;

                    if (
                        i == j ||
                        (
                            i +
                            1
                        ) %
                        polygon.Count ==
                        j ||
                        i ==
                        nextJ
                    )
                    {
                        continue;
                    }

                    Vector2 b1 =
                        polygon[j];

                    Vector2 b2 =
                        polygon[
                            nextJ
                        ];

                    if (
                        SegmentsIntersect2D(
                            a1,
                            a2,
                            b1,
                            b2
                        )
                    )
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SegmentsIntersect2D(
            Vector2 a,
            Vector2 b,
            Vector2 c,
            Vector2 d
        )
        {
            float abC =
                Cross2D(
                    b -
                    a,
                    c -
                    a
                );

            float abD =
                Cross2D(
                    b -
                    a,
                    d -
                    a
                );

            float cdA =
                Cross2D(
                    d -
                    c,
                    a -
                    c
                );

            float cdB =
                Cross2D(
                    d -
                    c,
                    b -
                    c
                );

            return
                (
                    abC *
                    abD <
                    -0.000001f
                ) &&
                (
                    cdA *
                    cdB <
                    -0.000001f
                );
        }

        private static float Cross2D(
            Vector2 a,
            Vector2 b
        )
        {
            return
                a.x *
                b.y -
                a.y *
                b.x;
        }

        private void BuildFloorRasterProfiles(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            visual.FloorRasterProfiles.Clear();

            int floorBoundaryCount =
                visual.FloorBoundaries != null
                    ? visual.FloorBoundaries.Count
                    : 0;

            if (
                floorBoundaryCount == 0 ||
                visual.Footprint == null ||
                visual.Footprint.Count < 3
            )
            {
                return;
            }

            Bounds2D footprintBounds =
                CalculateBounds2D(
                    visual.Footprint
                );

            float maxDimension =
                Mathf.Max(
                    footprintBounds.Width,
                    footprintBounds.Depth
                );

            float cellSize =
                Mathf.Clamp(
                    maxDimension /
                    36f,
                    0.80f,
                    1.40f
                );

            float margin =
                cellSize *
                1.5f;

            float minX =
                footprintBounds.Min.x -
                margin;

            float minZ =
                footprintBounds.Min.y -
                margin;

            int width =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        (
                            footprintBounds.Width +
                            margin *
                            2f
                        ) /
                        cellSize
                    ),
                    4,
                    48
                );

            int height =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        (
                            footprintBounds.Depth +
                            margin *
                            2f
                        ) /
                        cellSize
                    ),
                    4,
                    48
                );

            FloorRasterProfile previous =
                BuildRasterFromPolygon(
                    visual.Footprint,
                    minX,
                    minZ,
                    cellSize,
                    width,
                    height
                );

            for (
                int floorIndex = 0;
                floorIndex < floorBoundaryCount;
                floorIndex++
            )
            {
                FloorRasterProfile accepted =
                    null;

                string decision =
                    "reuse-base";

                int segmentCount =
                    0;

                if (
                    visual.StructureTriangleVertices != null &&
                    visual.StructureTriangleVertices.Count >= 3
                )
                {
                    float localHeight =
                        visual.FloorBoundaries[floorIndex];

                    float sampleOffset =
                        floorIndex == 0
                            ? 0.12f
                            : -0.12f;

                    float planeY =
                        visual.StructureGeometryBaseY +
                        Mathf.Clamp(
                            localHeight +
                            sampleOffset,
                            0.05f,
                            Mathf.Max(
                                0.05f,
                                visual.BuildingHeight -
                                0.05f
                            )
                        );

                    List<SliceSegment> segments =
                        new List<SliceSegment>();

                    for (
                        int triangleIndex = 0;
                        triangleIndex + 2 < visual.StructureTriangleVertices.Count;
                        triangleIndex += 3
                    )
                    {
                        AddTriangleSliceSegments(
                            visual.StructureTriangleVertices[triangleIndex],
                            visual.StructureTriangleVertices[triangleIndex + 1],
                            visual.StructureTriangleVertices[triangleIndex + 2],
                            planeY,
                            segments
                        );
                    }

                    segmentCount =
                        segments.Count;

                    FloorRasterProfile candidate =
                        BuildRasterFromSliceSegments(
                            segments,
                            minX,
                            minZ,
                            cellSize,
                            width,
                            height
                        );

                    CleanupRasterMask(
                        candidate
                    );

                    KeepLargestRasterComponent(
                        candidate
                    );

                    float previousArea =
                        Mathf.Max(
                            0.01f,
                            previous.OccupiedCount *
                            cellSize *
                            cellSize
                        );

                    float candidateArea =
                        candidate.OccupiedCount *
                        cellSize *
                        cellSize;

                    float ratio =
                        candidateArea /
                        previousArea;

                    bool plausible =
                        candidate.OccupiedCount >=
                            4 &&
                        ratio >=
                            0.35f &&
                        ratio <=
                            1.25f;

                    if (
                        plausible
                    )
                    {
                        float difference =
                            CalculateRasterDifference(
                                previous,
                                candidate
                            );

                        if (
                            difference <
                            0.04f
                        )
                        {
                            accepted =
                                CloneRasterProfile(
                                    previous
                                );

                            decision =
                                "reuse-near-identical";
                        }
                        else
                        {
                            accepted =
                                candidate;

                            decision =
                                "accept-raster";
                        }
                    }
                    else
                    {
                        accepted =
                            CloneRasterProfile(
                                previous
                            );

                        decision =
                            "reject-raster";
                    }
                }
                else
                {
                    accepted =
                        CloneRasterProfile(
                            previous
                        );

                    decision =
                        "fallback-base";
                }

                if (
                    accepted == null
                )
                {
                    accepted =
                        CloneRasterProfile(
                            previous
                        );
                }

                visual.FloorRasterProfiles.Add(
                    accepted
                );

                previous =
                    accepted;

                ModLog.Checkpoint(
                    "STRUCTURE-RASTER; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; floor=" +
                    floorIndex +
                    "; grid=" +
                    width +
                    "x" +
                    height +
                    "; cell=" +
                    cellSize.ToString(
                        "0.00"
                    ) +
                    "; segments=" +
                    segmentCount +
                    "; occupied=" +
                    accepted.OccupiedCount +
                    "; decision=" +
                    decision
                );
            }
        }

        private struct Bounds2D
        {
            public Vector2 Min;
            public Vector2 Max;

            public float Width
            {
                get
                {
                    return
                        Max.x -
                        Min.x;
                }
            }

            public float Depth
            {
                get
                {
                    return
                        Max.y -
                        Min.y;
                }
            }

            public Vector2 Center
            {
                get
                {
                    return
                        (
                            Min +
                            Max
                        ) *
                        0.5f;
                }
            }
        }

        private static Bounds2D CalculateBounds2D(
            List<Vector2> polygon
        )
        {
            Bounds2D result =
                new Bounds2D
                {
                    Min =
                        new Vector2(
                            float.MaxValue,
                            float.MaxValue
                        ),
                    Max =
                        new Vector2(
                            float.MinValue,
                            float.MinValue
                        )
                };

            if (
                polygon == null ||
                polygon.Count == 0
            )
            {
                result.Min =
                    Vector2.zero;

                result.Max =
                    Vector2.zero;

                return result;
            }

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 p =
                    polygon[i];

                result.Min =
                    Vector2.Min(
                        result.Min,
                        p
                    );

                result.Max =
                    Vector2.Max(
                        result.Max,
                        p
                    );
            }

            return result;
        }

        private static FloorRasterProfile BuildRasterFromPolygon(
            List<Vector2> polygon,
            float minX,
            float minZ,
            float cellSize,
            int width,
            int height
        )
        {
            FloorRasterProfile result =
                new FloorRasterProfile
                {
                    MinX =
                        minX,
                    MinZ =
                        minZ,
                    CellSize =
                        cellSize,
                    Width =
                        width,
                    Height =
                        height,
                    OccupiedCells =
                        new bool[
                            width *
                            height
                        ]
                };

            for (
                int z = 0;
                z < height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < width;
                    x++
                )
                {
                    Vector2 point =
                        new Vector2(
                            minX +
                            (
                                x +
                                0.5f
                            ) *
                            cellSize,
                            minZ +
                            (
                                z +
                                0.5f
                            ) *
                            cellSize
                        );

                    if (
                        PointInPolygon2D(
                            point,
                            polygon
                        )
                    )
                    {
                        result.OccupiedCells[
                            z *
                            width +
                            x
                        ] =
                            true;

                        result.OccupiedCount++;
                    }
                }
            }

            return result;
        }

        private static FloorRasterProfile BuildRasterFromSliceSegments(
            List<SliceSegment> segments,
            float minX,
            float minZ,
            float cellSize,
            int width,
            int height
        )
        {
            FloorRasterProfile result =
                new FloorRasterProfile
                {
                    MinX =
                        minX,
                    MinZ =
                        minZ,
                    CellSize =
                        cellSize,
                    Width =
                        width,
                    Height =
                        height,
                    OccupiedCells =
                        new bool[
                            width *
                            height
                        ]
                };

            if (
                segments == null ||
                segments.Count == 0
            )
            {
                return result;
            }

            List<float> intersections =
                new List<float>();

            for (
                int z = 0;
                z < height;
                z++
            )
            {
                intersections.Clear();

                float rowZ =
                    minZ +
                    (
                        z +
                        0.5f
                    ) *
                    cellSize;

                for (
                    int i = 0;
                    i < segments.Count;
                    i++
                )
                {
                    Vector2 a =
                        segments[i].A;

                    Vector2 b =
                        segments[i].B;

                    float dz =
                        b.y -
                        a.y;

                    if (
                        Mathf.Abs(
                            dz
                        ) <
                        0.00001f
                    )
                    {
                        continue;
                    }

                    bool crosses =
                        (
                            rowZ >=
                            Mathf.Min(
                                a.y,
                                b.y
                            )
                        ) &&
                        (
                            rowZ <
                            Mathf.Max(
                                a.y,
                                b.y
                            )
                        );

                    if (
                        !crosses
                    )
                    {
                        continue;
                    }

                    float t =
                        (
                            rowZ -
                            a.y
                        ) /
                        dz;

                    float xHit =
                        Mathf.Lerp(
                            a.x,
                            b.x,
                            t
                        );

                    intersections.Add(
                        xHit
                    );
                }

                if (
                    intersections.Count < 2
                )
                {
                    continue;
                }

                intersections.Sort();

                List<float> unique =
                    new List<float>();

                for (
                    int i = 0;
                    i < intersections.Count;
                    i++
                )
                {
                    if (
                        unique.Count == 0 ||
                        Mathf.Abs(
                            intersections[i] -
                            unique[
                                unique.Count -
                                1
                            ]
                        ) >
                        0.05f
                    )
                    {
                        unique.Add(
                            intersections[i]
                        );
                    }
                }

                if (
                    unique.Count <
                    2
                )
                {
                    continue;
                }

                if (
                    unique.Count %
                    2 !=
                    0
                )
                {
                    unique.RemoveAt(
                        unique.Count -
                        1
                    );
                }

                for (
                    int pair = 0;
                    pair + 1 < unique.Count;
                    pair += 2
                )
                {
                    float startX =
                        unique[pair];

                    float endX =
                        unique[pair + 1];

                    if (
                        endX <
                        startX
                    )
                    {
                        float swap =
                            startX;

                        startX =
                            endX;

                        endX =
                            swap;
                    }

                    for (
                        int x = 0;
                        x < width;
                        x++
                    )
                    {
                        float centerX =
                            minX +
                            (
                                x +
                                0.5f
                            ) *
                            cellSize;

                        if (
                            centerX >=
                                startX -
                                cellSize *
                                0.20f &&
                            centerX <=
                                endX +
                                cellSize *
                                0.20f
                        )
                        {
                            int index =
                                z *
                                width +
                                x;

                            if (
                                !result.OccupiedCells[index]
                            )
                            {
                                result.OccupiedCells[index] =
                                    true;

                                result.OccupiedCount++;
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static void CleanupRasterMask(
            FloorRasterProfile profile
        )
        {
            if (
                profile == null ||
                profile.OccupiedCells == null
            )
            {
                return;
            }

            bool[] source =
                profile.OccupiedCells;

            bool[] cleaned =
                new bool[
                    source.Length
                ];

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    int occupiedNeighbours =
                        0;

                    for (
                        int dz = -1;
                        dz <= 1;
                        dz++
                    )
                    {
                        for (
                            int dx = -1;
                            dx <= 1;
                            dx++
                        )
                        {
                            if (
                                dx == 0 &&
                                dz == 0
                            )
                            {
                                continue;
                            }

                            if (
                                profile.IsOccupied(
                                    x +
                                    dx,
                                    z +
                                    dz
                                )
                            )
                            {
                                occupiedNeighbours++;
                            }
                        }
                    }

                    bool occupied =
                        profile.IsOccupied(
                            x,
                            z
                        );

                    cleaned[
                        z *
                        profile.Width +
                        x
                    ] =
                        occupied
                            ? occupiedNeighbours >= 2
                            : occupiedNeighbours >= 6;
                }
            }

            profile.OccupiedCells =
                cleaned;

            RecountRaster(
                profile
            );
        }

        private static void KeepLargestRasterComponent(
            FloorRasterProfile profile
        )
        {
            if (
                profile == null ||
                profile.OccupiedCells == null ||
                profile.OccupiedCount == 0
            )
            {
                return;
            }

            bool[] visited =
                new bool[
                    profile.OccupiedCells.Length
                ];

            List<int> best =
                new List<int>();

            int[] dirX =
                new int[]
                {
                    1,
                    -1,
                    0,
                    0
                };

            int[] dirZ =
                new int[]
                {
                    0,
                    0,
                    1,
                    -1
                };

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    int startIndex =
                        z *
                        profile.Width +
                        x;

                    if (
                        visited[startIndex] ||
                        !profile.OccupiedCells[startIndex]
                    )
                    {
                        continue;
                    }

                    List<int> queue =
                        new List<int>();

                    int queueHead =
                        0;

                    List<int> component =
                        new List<int>();

                    visited[startIndex] =
                        true;

                    queue.Add(
                        startIndex
                    );

                    while (
                        queueHead <
                        queue.Count
                    )
                    {
                        int current =
                            queue[
                                queueHead
                            ];

                        queueHead++;

                        component.Add(
                            current
                        );

                        int currentX =
                            current %
                            profile.Width;

                        int currentZ =
                            current /
                            profile.Width;

                        for (
                            int direction = 0;
                            direction < 4;
                            direction++
                        )
                        {
                            int nextX =
                                currentX +
                                dirX[direction];

                            int nextZ =
                                currentZ +
                                dirZ[direction];

                            if (
                                nextX < 0 ||
                                nextZ < 0 ||
                                nextX >=
                                    profile.Width ||
                                nextZ >=
                                    profile.Height
                            )
                            {
                                continue;
                            }

                            int nextIndex =
                                nextZ *
                                profile.Width +
                                nextX;

                            if (
                                visited[nextIndex] ||
                                !profile.OccupiedCells[nextIndex]
                            )
                            {
                                continue;
                            }

                            visited[nextIndex] =
                                true;

                            queue.Add(
                                nextIndex
                            );
                        }
                    }

                    if (
                        component.Count >
                        best.Count
                    )
                    {
                        best =
                            component;
                    }
                }
            }

            bool[] largestOnly =
                new bool[
                    profile.OccupiedCells.Length
                ];

            for (
                int i = 0;
                i < best.Count;
                i++
            )
            {
                largestOnly[
                    best[i]
                ] =
                    true;
            }

            profile.OccupiedCells =
                largestOnly;

            profile.OccupiedCount =
                best.Count;
        }

        private static FloorRasterProfile CloneRasterProfile(
            FloorRasterProfile source
        )
        {
            if (
                source == null
            )
            {
                return null;
            }

            return
                new FloorRasterProfile
                {
                    MinX =
                        source.MinX,
                    MinZ =
                        source.MinZ,
                    CellSize =
                        source.CellSize,
                    Width =
                        source.Width,
                    Height =
                        source.Height,
                    OccupiedCells =
                        source.OccupiedCells != null
                            ? (
                                bool[]
                            )source.OccupiedCells.Clone()
                            : null,
                    OccupiedCount =
                        source.OccupiedCount
                };
        }

        private static void RecountRaster(
            FloorRasterProfile profile
        )
        {
            if (
                profile == null ||
                profile.OccupiedCells == null
            )
            {
                return;
            }

            int count =
                0;

            for (
                int i = 0;
                i < profile.OccupiedCells.Length;
                i++
            )
            {
                if (
                    profile.OccupiedCells[i]
                )
                {
                    count++;
                }
            }

            profile.OccupiedCount =
                count;
        }

        private static float CalculateRasterDifference(
            FloorRasterProfile a,
            FloorRasterProfile b
        )
        {
            if (
                a == null ||
                b == null ||
                a.Width != b.Width ||
                a.Height != b.Height ||
                a.OccupiedCells == null ||
                b.OccupiedCells == null
            )
            {
                return 1f;
            }

            int union =
                0;

            int difference =
                0;

            for (
                int i = 0;
                i < a.OccupiedCells.Length;
                i++
            )
            {
                bool av =
                    a.OccupiedCells[i];

                bool bv =
                    b.OccupiedCells[i];

                if (
                    av ||
                    bv
                )
                {
                    union++;
                }

                if (
                    av !=
                    bv
                )
                {
                    difference++;
                }
            }

            return
                union > 0
                    ? difference /
                    (float)union
                    : 0f;
        }

        private static bool PointInPolygon2D(
            Vector2 point,
            List<Vector2> polygon
        )
        {
            if (
                polygon == null ||
                polygon.Count < 3
            )
            {
                return false;
            }

            bool inside =
                false;

            int j =
                polygon.Count -
                1;

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                Vector2 pi =
                    polygon[i];

                Vector2 pj =
                    polygon[j];

                bool intersects =
                    (
                        (
                            pi.y >
                            point.y
                        ) !=
                        (
                            pj.y >
                            point.y
                        )
                    ) &&
                    (
                        point.x <
                        (
                            pj.x -
                            pi.x
                        ) *
                        (
                            point.y -
                            pi.y
                        ) /
                        Mathf.Max(
                            0.000001f,
                            pj.y -
                            pi.y
                        ) +
                        pi.x
                    );

                if (
                    intersects
                )
                {
                    inside =
                        !inside;
                }

                j =
                    i;
            }

            return inside;
        }

        private static void AddTriangleSliceSegments(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            float planeY,
            List<SliceSegment> output
        )
        {
            const float epsilon =
                0.0025f;

            if (
                output == null
            )
            {
                return;
            }

            float d0 =
                p0.y -
                planeY;

            float d1 =
                p1.y -
                planeY;

            float d2 =
                p2.y -
                planeY;

            if (
                Mathf.Abs(
                    d0
                ) <= epsilon &&
                Mathf.Abs(
                    d1
                ) <= epsilon &&
                Mathf.Abs(
                    d2
                ) <= epsilon
            )
            {
                return;
            }

            List<Vector2> intersections =
                new List<Vector2>(
                    3
                );

            AddSliceEdgeIntersection(
                p0,
                p1,
                d0,
                d1,
                epsilon,
                intersections
            );

            AddSliceEdgeIntersection(
                p1,
                p2,
                d1,
                d2,
                epsilon,
                intersections
            );

            AddSliceEdgeIntersection(
                p2,
                p0,
                d2,
                d0,
                epsilon,
                intersections
            );

            for (
                int i = intersections.Count - 1;
                i >= 0;
                i--
            )
            {
                for (
                    int j = 0;
                    j < i;
                    j++
                )
                {
                    if (
                        (
                            intersections[i] -
                            intersections[j]
                        ).sqrMagnitude <
                        0.000025f
                    )
                    {
                        intersections.RemoveAt(
                            i
                        );

                        break;
                    }
                }
            }

            if (
                intersections.Count == 2 &&
                (
                    intersections[0] -
                    intersections[1]
                ).sqrMagnitude >
                0.0001f
            )
            {
                output.Add(
                    new SliceSegment
                    {
                        A =
                            intersections[0],
                        B =
                            intersections[1]
                    }
                );
            }
        }

        private static void AddSliceEdgeIntersection(
            Vector3 a,
            Vector3 b,
            float da,
            float db,
            float epsilon,
            List<Vector2> output
        )
        {
            if (
                output == null
            )
            {
                return;
            }

            bool aOn =
                Mathf.Abs(
                    da
                ) <= epsilon;

            bool bOn =
                Mathf.Abs(
                    db
                ) <= epsilon;

            if (
                aOn &&
                bOn
            )
            {
                return;
            }

            if (
                aOn
            )
            {
                output.Add(
                    new Vector2(
                        a.x,
                        a.z
                    )
                );

                return;
            }

            if (
                bOn
            )
            {
                output.Add(
                    new Vector2(
                        b.x,
                        b.z
                    )
                );

                return;
            }

            if (
                (
                    da < 0f &&
                    db < 0f
                ) ||
                (
                    da > 0f &&
                    db > 0f
                )
            )
            {
                return;
            }

            float denominator =
                da -
                db;

            if (
                Mathf.Abs(
                    denominator
                ) <
                0.000001f
            )
            {
                return;
            }

            float t =
                da /
                denominator;

            Vector3 point =
                Vector3.Lerp(
                    a,
                    b,
                    t
                );

            output.Add(
                new Vector2(
                    point.x,
                    point.z
                )
            );
        }

        private static string SlicePointKey(
            Vector2 point
        )
        {
            const float precision =
                50f;

            int x =
                Mathf.RoundToInt(
                    point.x *
                    precision
                );

            int y =
                Mathf.RoundToInt(
                    point.y *
                    precision
                );

            return
                x +
                ":" +
                y;
        }

        private static List<List<Vector2>> BuildSliceLoops(
            List<SliceSegment> segments
        )
        {
            List<List<Vector2>> loops =
                new List<List<Vector2>>();

            if (
                segments == null ||
                segments.Count == 0
            )
            {
                return loops;
            }

            Dictionary<string, List<int>> adjacency =
                new Dictionary<string, List<int>>();

            for (
                int i = 0;
                i < segments.Count;
                i++
            )
            {
                string keyA =
                    SlicePointKey(
                        segments[i].A
                    );

                string keyB =
                    SlicePointKey(
                        segments[i].B
                    );

                if (
                    !adjacency.TryGetValue(
                        keyA,
                        out List<int> listA
                    )
                )
                {
                    listA =
                        new List<int>();

                    adjacency[
                        keyA
                    ] =
                        listA;
                }

                if (
                    !adjacency.TryGetValue(
                        keyB,
                        out List<int> listB
                    )
                )
                {
                    listB =
                        new List<int>();

                    adjacency[
                        keyB
                    ] =
                        listB;
                }

                listA.Add(
                    i
                );

                listB.Add(
                    i
                );
            }

            bool[] used =
                new bool[
                    segments.Count
                ];

            for (
                int startSegmentIndex = 0;
                startSegmentIndex < segments.Count;
                startSegmentIndex++
            )
            {
                if (
                    used[startSegmentIndex]
                )
                {
                    continue;
                }

                SliceSegment startSegment =
                    segments[startSegmentIndex];

                List<Vector2> loop =
                    new List<Vector2>();

                loop.Add(
                    startSegment.A
                );

                loop.Add(
                    startSegment.B
                );

                used[startSegmentIndex] =
                    true;

                Vector2 current =
                    startSegment.B;

                string startKey =
                    SlicePointKey(
                        startSegment.A
                    );

                int guard =
                    0;

                while (
                    guard <
                    segments.Count +
                    8
                )
                {
                    guard++;

                    string currentKey =
                        SlicePointKey(
                            current
                        );

                    if (
                        currentKey ==
                        startKey
                    )
                    {
                        break;
                    }

                    if (
                        !adjacency.TryGetValue(
                            currentKey,
                            out List<int> candidates
                        )
                    )
                    {
                        break;
                    }

                    int nextIndex =
                        -1;

                    for (
                        int candidateIndex = 0;
                        candidateIndex < candidates.Count;
                        candidateIndex++
                    )
                    {
                        int segmentIndex =
                            candidates[candidateIndex];

                        if (
                            !used[segmentIndex]
                        )
                        {
                            nextIndex =
                                segmentIndex;

                            break;
                        }
                    }

                    if (
                        nextIndex <
                        0
                    )
                    {
                        break;
                    }

                    SliceSegment next =
                        segments[nextIndex];

                    used[nextIndex] =
                        true;

                    string nextAKey =
                        SlicePointKey(
                            next.A
                        );

                    Vector2 nextPoint =
                        nextAKey ==
                        currentKey
                            ? next.B
                            : next.A;

                    loop.Add(
                        nextPoint
                    );

                    current =
                        nextPoint;
                }

                if (
                    loop.Count >= 4 &&
                    SlicePointKey(
                        loop[
                            loop.Count -
                            1
                        ]
                    ) ==
                    startKey
                )
                {
                    loop.RemoveAt(
                        loop.Count -
                        1
                    );
                }

                if (
                    loop.Count >= 3
                )
                {
                    loops.Add(
                        loop
                    );
                }
            }

            return loops;
        }

        private static List<Vector2> SelectLargestSliceLoop(
            List<List<Vector2>> loops
        )
        {
            if (
                loops == null ||
                loops.Count == 0
            )
            {
                return null;
            }

            List<Vector2> best =
                null;

            float bestArea =
                0f;

            for (
                int i = 0;
                i < loops.Count;
                i++
            )
            {
                List<Vector2> loop =
                    loops[i];

                if (
                    loop == null ||
                    loop.Count < 3
                )
                {
                    continue;
                }

                float area =
                    Mathf.Abs(
                        SignedPolygonArea(
                            loop
                        )
                    );

                if (
                    area >
                    bestArea
                )
                {
                    bestArea =
                        area;

                    best =
                        loop;
                }
            }

            return best != null
                ? new List<Vector2>(
                    best
                )
                : null;
        }

        private static float SignedPolygonArea(
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

            float area =
                0f;

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

                area +=
                    a.x *
                    b.y -
                    b.x *
                    a.y;
            }

            return
                area *
                0.5f;
        }

        private static List<Vector2> SimplifySliceLoop(
            List<Vector2> input
        )
        {
            if (
                input == null ||
                input.Count < 3
            )
            {
                return input != null
                    ? new List<Vector2>(
                        input
                    )
                    : new List<Vector2>();
            }

            List<Vector2> result =
                new List<Vector2>();

            for (
                int i = 0;
                i < input.Count;
                i++
            )
            {
                Vector2 previous =
                    input[
                        (
                            i -
                            1 +
                            input.Count
                        ) %
                        input.Count
                    ];

                Vector2 current =
                    input[i];

                Vector2 next =
                    input[
                        (
                            i +
                            1
                        ) %
                        input.Count
                    ];

                if (
                    (
                        current -
                        previous
                    ).sqrMagnitude <
                    0.0004f
                )
                {
                    continue;
                }

                Vector2 dirA =
                    (
                        current -
                        previous
                    ).normalized;

                Vector2 dirB =
                    (
                        next -
                        current
                    ).normalized;

                float cross =
                    Mathf.Abs(
                        dirA.x *
                        dirB.y -
                        dirA.y *
                        dirB.x
                    );

                if (
                    cross <
                    0.01f &&
                    Vector2.Dot(
                        dirA,
                        dirB
                    ) >
                    0.98f
                )
                {
                    continue;
                }

                result.Add(
                    current
                );
            }

            return
                result.Count >= 3
                    ? result
                    : new List<Vector2>(
                        input
                    );
        }

        private static List<int> TriangulateSimplePolygon(
            List<Vector2> polygon
        )
        {
            List<int> triangles =
                new List<int>();

            if (
                polygon == null ||
                polygon.Count < 3
            )
            {
                return triangles;
            }

            List<int> remaining =
                new List<int>();

            for (
                int i = 0;
                i < polygon.Count;
                i++
            )
            {
                remaining.Add(
                    i
                );
            }

            if (
                SignedPolygonArea(
                    polygon
                ) < 0f
            )
            {
                remaining.Reverse();
            }

            int guard =
                0;

            while (
                remaining.Count > 3 &&
                guard <
                polygon.Count *
                polygon.Count
            )
            {
                guard++;

                bool clipped =
                    false;

                for (
                    int i = 0;
                    i < remaining.Count;
                    i++
                )
                {
                    int previousIndex =
                        remaining[
                            (
                                i -
                                1 +
                                remaining.Count
                            ) %
                            remaining.Count
                        ];

                    int currentIndex =
                        remaining[i];

                    int nextIndex =
                        remaining[
                            (
                                i +
                                1
                            ) %
                            remaining.Count
                        ];

                    Vector2 a =
                        polygon[previousIndex];

                    Vector2 b =
                        polygon[currentIndex];

                    Vector2 c =
                        polygon[nextIndex];

                    float cross =
                        (
                            b.x -
                            a.x
                        ) *
                        (
                            c.y -
                            b.y
                        ) -
                        (
                            b.y -
                            a.y
                        ) *
                        (
                            c.x -
                            b.x
                        );

                    if (
                        cross <=
                        0.00001f
                    )
                    {
                        continue;
                    }

                    bool containsPoint =
                        false;

                    for (
                        int test = 0;
                        test < remaining.Count;
                        test++
                    )
                    {
                        int testIndex =
                            remaining[test];

                        if (
                            testIndex ==
                            previousIndex ||
                            testIndex ==
                            currentIndex ||
                            testIndex ==
                            nextIndex
                        )
                        {
                            continue;
                        }

                        if (
                            PointInTriangle2D(
                                polygon[testIndex],
                                a,
                                b,
                                c
                            )
                        )
                        {
                            containsPoint =
                                true;

                            break;
                        }
                    }

                    if (
                        containsPoint
                    )
                    {
                        continue;
                    }

                    triangles.Add(
                        previousIndex
                    );

                    triangles.Add(
                        currentIndex
                    );

                    triangles.Add(
                        nextIndex
                    );

                    remaining.RemoveAt(
                        i
                    );

                    clipped =
                        true;

                    break;
                }

                if (
                    !clipped
                )
                {
                    break;
                }
            }

            if (
                remaining.Count == 3
            )
            {
                triangles.Add(
                    remaining[0]
                );

                triangles.Add(
                    remaining[1]
                );

                triangles.Add(
                    remaining[2]
                );
            }

            return triangles;
        }

        private static bool PointInTriangle2D(
            Vector2 p,
            Vector2 a,
            Vector2 b,
            Vector2 c
        )
        {
            float d1 =
                SignTriangle2D(
                    p,
                    a,
                    b
                );

            float d2 =
                SignTriangle2D(
                    p,
                    b,
                    c
                );

            float d3 =
                SignTriangle2D(
                    p,
                    c,
                    a
                );

            bool hasNegative =
                d1 <
                0f ||
                d2 <
                0f ||
                d3 <
                0f;

            bool hasPositive =
                d1 >
                0f ||
                d2 >
                0f ||
                d3 >
                0f;

            return
                !(
                    hasNegative &&
                    hasPositive
                );
        }

        private static float SignTriangle2D(
            Vector2 p1,
            Vector2 p2,
            Vector2 p3
        )
        {
            return
                (
                    p1.x -
                    p3.x
                ) *
                (
                    p2.y -
                    p3.y
                ) -
                (
                    p2.x -
                    p3.x
                ) *
                (
                    p1.y -
                    p3.y
                );
        }


        private GameObject CreateConcreteRasterSlab(
            GameObject parent,
            FloorRasterProfile profile,
            float centerY,
            float thickness,
            string name,
            ConstructionVisual visual
        )
        {
            if (
                parent == null ||
                profile == null ||
                profile.OccupiedCells == null ||
                profile.OccupiedCount == 0
            )
            {
                return null;
            }

            List<Vector3> vertices =
                new List<Vector3>();

            List<int> triangles =
                new List<int>();

            List<Vector2> uvs =
                new List<Vector2>();

            float half =
                thickness *
                0.5f;

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    if (
                        !profile.IsOccupied(
                            x,
                            z
                        )
                    )
                    {
                        continue;
                    }

                    float x0 =
                        profile.MinX +
                        x *
                        profile.CellSize;

                    float x1 =
                        x0 +
                        profile.CellSize;

                    float z0 =
                        profile.MinZ +
                        z *
                        profile.CellSize;

                    float z1 =
                        z0 +
                        profile.CellSize;

                    AppendHorizontalQuad(
                        vertices,
                        triangles,
                        uvs,
                        new Vector3(
                            x0,
                            centerY +
                            half,
                            z0
                        ),
                        new Vector3(
                            x1,
                            centerY +
                            half,
                            z0
                        ),
                        new Vector3(
                            x1,
                            centerY +
                            half,
                            z1
                        ),
                        new Vector3(
                            x0,
                            centerY +
                            half,
                            z1
                        ),
                        false
                    );

                    AppendHorizontalQuad(
                        vertices,
                        triangles,
                        uvs,
                        new Vector3(
                            x0,
                            centerY -
                            half,
                            z0
                        ),
                        new Vector3(
                            x0,
                            centerY -
                            half,
                            z1
                        ),
                        new Vector3(
                            x1,
                            centerY -
                            half,
                            z1
                        ),
                        new Vector3(
                            x1,
                            centerY -
                            half,
                            z0
                        ),
                        false
                    );

                    if (
                        !profile.IsOccupied(
                            x - 1,
                            z
                        )
                    )
                    {
                        AppendVerticalQuad(
                            vertices,
                            triangles,
                            uvs,
                            new Vector3(
                                x0,
                                centerY -
                                half,
                                z0
                            ),
                            new Vector3(
                                x0,
                                centerY -
                                half,
                                z1
                            ),
                            new Vector3(
                                x0,
                                centerY +
                                half,
                                z1
                            ),
                            new Vector3(
                                x0,
                                centerY +
                                half,
                                z0
                            )
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x + 1,
                            z
                        )
                    )
                    {
                        AppendVerticalQuad(
                            vertices,
                            triangles,
                            uvs,
                            new Vector3(
                                x1,
                                centerY -
                                half,
                                z1
                            ),
                            new Vector3(
                                x1,
                                centerY -
                                half,
                                z0
                            ),
                            new Vector3(
                                x1,
                                centerY +
                                half,
                                z0
                            ),
                            new Vector3(
                                x1,
                                centerY +
                                half,
                                z1
                            )
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x,
                            z - 1
                        )
                    )
                    {
                        AppendVerticalQuad(
                            vertices,
                            triangles,
                            uvs,
                            new Vector3(
                                x1,
                                centerY -
                                half,
                                z0
                            ),
                            new Vector3(
                                x0,
                                centerY -
                                half,
                                z0
                            ),
                            new Vector3(
                                x0,
                                centerY +
                                half,
                                z0
                            ),
                            new Vector3(
                                x1,
                                centerY +
                                half,
                                z0
                            )
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x,
                            z + 1
                        )
                    )
                    {
                        AppendVerticalQuad(
                            vertices,
                            triangles,
                            uvs,
                            new Vector3(
                                x0,
                                centerY -
                                half,
                                z1
                            ),
                            new Vector3(
                                x1,
                                centerY -
                                half,
                                z1
                            ),
                            new Vector3(
                                x1,
                                centerY +
                                half,
                                z1
                            ),
                            new Vector3(
                                x0,
                                centerY +
                                half,
                                z1
                            )
                        );
                    }
                }
            }

            return
                CombineConcreteFloorGeometry(
                    parent,
                    name,
                    vertices,
                    triangles,
                    uvs,
                    visual
                );
        }

        private static void AppendHorizontalQuad(
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            bool reverse
        )
        {
            int start =
                vertices.Count;

            vertices.Add(
                a
            );

            vertices.Add(
                b
            );

            vertices.Add(
                c
            );

            vertices.Add(
                d
            );

            uvs.Add(
                new Vector2(
                    a.x,
                    a.z
                ) *
                0.25f
            );

            uvs.Add(
                new Vector2(
                    b.x,
                    b.z
                ) *
                0.25f
            );

            uvs.Add(
                new Vector2(
                    c.x,
                    c.z
                ) *
                0.25f
            );

            uvs.Add(
                new Vector2(
                    d.x,
                    d.z
                ) *
                0.25f
            );

            if (
                reverse
            )
            {
                triangles.Add(
                    start
                );
                triangles.Add(
                    start +
                    2
                );
                triangles.Add(
                    start +
                    1
                );
                triangles.Add(
                    start
                );
                triangles.Add(
                    start +
                    3
                );
                triangles.Add(
                    start +
                    2
                );
            }
            else
            {
                triangles.Add(
                    start
                );
                triangles.Add(
                    start +
                    1
                );
                triangles.Add(
                    start +
                    2
                );
                triangles.Add(
                    start
                );
                triangles.Add(
                    start +
                    2
                );
                triangles.Add(
                    start +
                    3
                );
            }
        }

        private static void AppendVerticalQuad(
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d
        )
        {
            int start =
                vertices.Count;

            vertices.Add(
                a
            );
            vertices.Add(
                b
            );
            vertices.Add(
                c
            );
            vertices.Add(
                d
            );

            float length =
                Vector3.Distance(
                    a,
                    b
                );

            float height =
                Vector3.Distance(
                    a,
                    d
                );

            uvs.Add(
                new Vector2(
                    0f,
                    0f
                )
            );
            uvs.Add(
                new Vector2(
                    length *
                    0.25f,
                    0f
                )
            );
            uvs.Add(
                new Vector2(
                    length *
                    0.25f,
                    height *
                    0.25f
                )
            );
            uvs.Add(
                new Vector2(
                    0f,
                    height *
                    0.25f
                )
            );

            triangles.Add(
                start
            );
            triangles.Add(
                start +
                1
            );
            triangles.Add(
                start +
                2
            );
            triangles.Add(
                start
            );
            triangles.Add(
                start +
                2
            );
            triangles.Add(
                start +
                3
            );
        }

        private static List<Vector2> BuildRasterBoundaryColumnPoints(
            FloorRasterProfile profile
        )
        {
            List<Vector2> result =
                new List<Vector2>();

            if (
                profile == null ||
                profile.OccupiedCells == null
            )
            {
                return result;
            }

            HashSet<string> used =
                new HashSet<string>();

            float minimumSpacing =
                Mathf.Max(
                    4.5f,
                    profile.CellSize *
                    4f
                );

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    if (
                        !profile.IsOccupied(
                            x,
                            z
                        )
                    )
                    {
                        continue;
                    }

                    bool boundary =
                        !profile.IsOccupied(
                            x - 1,
                            z
                        ) ||
                        !profile.IsOccupied(
                            x + 1,
                            z
                        ) ||
                        !profile.IsOccupied(
                            x,
                            z - 1
                        ) ||
                        !profile.IsOccupied(
                            x,
                            z + 1
                        );

                    if (
                        !boundary
                    )
                    {
                        continue;
                    }

                    Vector2 point =
                        new Vector2(
                            profile.MinX +
                            (
                                x +
                                0.5f
                            ) *
                            profile.CellSize,
                            profile.MinZ +
                            (
                                z +
                                0.5f
                            ) *
                            profile.CellSize
                        );

                    bool tooClose =
                        false;

                    for (
                        int i = 0;
                        i < result.Count;
                        i++
                    )
                    {
                        if (
                            Vector2.Distance(
                                result[i],
                                point
                            ) <
                            minimumSpacing
                        )
                        {
                            tooClose =
                                true;

                            break;
                        }
                    }

                    if (
                        tooClose
                    )
                    {
                        continue;
                    }

                    string key =
                        SlicePointKey(
                            point
                        );

                    if (
                        used.Add(
                            key
                        )
                    )
                    {
                        result.Add(
                            point
                        );
                    }

                    if (
                        result.Count >=
                        MaxConcreteColumnsPerFloor
                    )
                    {
                        return result;
                    }
                }
            }

            return result;
        }

        private static void AppendRasterBoundaryBeams(
            FloorRasterProfile profile,
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            float thickness
        )
        {
            if (
                profile == null ||
                profile.OccupiedCells == null
            )
            {
                return;
            }

            for (
                int z = 0;
                z < profile.Height;
                z++
            )
            {
                for (
                    int x = 0;
                    x < profile.Width;
                    x++
                )
                {
                    if (
                        !profile.IsOccupied(
                            x,
                            z
                        )
                    )
                    {
                        continue;
                    }

                    float x0 =
                        profile.MinX +
                        x *
                        profile.CellSize;

                    float x1 =
                        x0 +
                        profile.CellSize;

                    float z0 =
                        profile.MinZ +
                        z *
                        profile.CellSize;

                    float z1 =
                        z0 +
                        profile.CellSize;

                    if (
                        !profile.IsOccupied(
                            x,
                            z - 1
                        )
                    )
                    {
                        AppendOrientedConcreteBeam(
                            vertices,
                            triangles,
                            uvs,
                            new Vector2(
                                x0,
                                z0
                            ),
                            new Vector2(
                                x1,
                                z0
                            ),
                            0f,
                            thickness
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x,
                            z + 1
                        )
                    )
                    {
                        AppendOrientedConcreteBeam(
                            vertices,
                            triangles,
                            uvs,
                            new Vector2(
                                x1,
                                z1
                            ),
                            new Vector2(
                                x0,
                                z1
                            ),
                            0f,
                            thickness
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x - 1,
                            z
                        )
                    )
                    {
                        AppendOrientedConcreteBeam(
                            vertices,
                            triangles,
                            uvs,
                            new Vector2(
                                x0,
                                z1
                            ),
                            new Vector2(
                                x0,
                                z0
                            ),
                            0f,
                            thickness
                        );
                    }

                    if (
                        !profile.IsOccupied(
                            x + 1,
                            z
                        )
                    )
                    {
                        AppendOrientedConcreteBeam(
                            vertices,
                            triangles,
                            uvs,
                            new Vector2(
                                x1,
                                z0
                            ),
                            new Vector2(
                                x1,
                                z1
                            ),
                            0f,
                            thickness
                        );
                    }
                }
            }
        }

        private const int MaxConcreteColumnsPerFloor =
            24;

        private GameObject CombineConcreteFloorGeometry(
            GameObject parent,
            string name,
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            ConstructionVisual visual
        )
        {
            if (
                parent == null ||
                vertices == null ||
                triangles == null ||
                vertices.Count == 0 ||
                triangles.Count < 3
            )
            {
                return null;
            }

            Mesh mesh =
                new Mesh();

            mesh.name =
                name +
                "_Mesh";

            if (
                vertices.Count >
                65000
            )
            {
                mesh.indexFormat =
                    UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(
                vertices
            );

            if (
                uvs != null &&
                uvs.Count ==
                    vertices.Count
            )
            {
                mesh.SetUVs(
                    0,
                    uvs
                );
            }

            mesh.SetTriangles(
                triangles,
                0
            );

            mesh.RecalculateNormals();

            mesh.RecalculateBounds();

            GameObject result =
                new GameObject(
                    name
                );

            result.hideFlags =
                HideFlags.DontSave;

            result.transform.SetParent(
                parent.transform,
                false
            );

            MeshFilter filter =
                result.AddComponent<MeshFilter>();

            filter.sharedMesh =
                mesh;

            MeshRenderer renderer =
                result.AddComponent<MeshRenderer>();

            renderer.sharedMaterial =
                m_BuildingConstructionMaterial;

            ConfigureStructureRenderer(
                renderer
            );

            if (
                visual != null
            )
            {
                visual.BuildingVisualMeshes.Add(
                    mesh
                );
            }

            return result;
        }

        private static void AppendAxisAlignedConcreteBox(
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            Vector3 min,
            Vector3 max
        )
        {
            int baseIndex =
                vertices.Count;

            Vector3[] boxVertices =
                new Vector3[]
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, max.z),
                    new Vector3(min.x, max.y, max.z)
                };

            for (
                int i = 0;
                i < boxVertices.Length;
                i++
            )
            {
                vertices.Add(
                    boxVertices[i]
                );

                uvs.Add(
                    new Vector2(
                        boxVertices[i].x,
                        boxVertices[i].z
                    ) *
                    0.25f
                );
            }

            int[] indices =
                new int[]
                {
                    0, 2, 1, 0, 3, 2,
                    4, 5, 6, 4, 6, 7,
                    0, 1, 5, 0, 5, 4,
                    3, 7, 6, 3, 6, 2,
                    1, 2, 6, 1, 6, 5,
                    0, 4, 7, 0, 7, 3
                };

            for (
                int i = 0;
                i < indices.Length;
                i++
            )
            {
                triangles.Add(
                    baseIndex +
                    indices[i]
                );
            }
        }

        private static void AppendOrientedConcreteBeam(
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector2> uvs,
            Vector2 a,
            Vector2 b,
            float centerY,
            float thickness
        )
        {
            Vector2 direction =
                b -
                a;

            float length =
                direction.magnitude;

            if (
                length <
                0.01f
            )
            {
                return;
            }

            direction /=
                length;

            Vector2 side =
                new Vector2(
                    -direction.y,
                    direction.x
                ) *
                (
                    thickness *
                    0.5f
                );

            float halfY =
                thickness *
                0.5f;

            Vector2 aLeft =
                a -
                side;
            Vector2 aRight =
                a +
                side;
            Vector2 bLeft =
                b -
                side;
            Vector2 bRight =
                b +
                side;

            int baseIndex =
                vertices.Count;

            Vector3[] beamVertices =
                new Vector3[]
                {
                    new Vector3(aLeft.x, centerY - halfY, aLeft.y),
                    new Vector3(aRight.x, centerY - halfY, aRight.y),
                    new Vector3(aRight.x, centerY + halfY, aRight.y),
                    new Vector3(aLeft.x, centerY + halfY, aLeft.y),
                    new Vector3(bLeft.x, centerY - halfY, bLeft.y),
                    new Vector3(bRight.x, centerY - halfY, bRight.y),
                    new Vector3(bRight.x, centerY + halfY, bRight.y),
                    new Vector3(bLeft.x, centerY + halfY, bLeft.y)
                };

            for (
                int i = 0;
                i < beamVertices.Length;
                i++
            )
            {
                vertices.Add(
                    beamVertices[i]
                );

                float along =
                    i >= 4
                        ? length
                        : 0f;

                float vertical =
                    (
                        i == 2 ||
                        i == 3 ||
                        i == 6 ||
                        i == 7
                    )
                        ? thickness
                        : 0f;

                uvs.Add(
                    new Vector2(
                        along *
                        0.25f,
                        vertical *
                        0.25f
                    )
                );
            }

            int[] indices =
                new int[]
                {
                    0, 2, 1, 0, 3, 2,
                    4, 5, 6, 4, 6, 7,
                    0, 1, 5, 0, 5, 4,
                    3, 7, 6, 3, 6, 2,
                    1, 2, 6, 1, 6, 5,
                    0, 4, 7, 0, 7, 3
                };

            for (
                int i = 0;
                i < indices.Length;
                i++
            )
            {
                triangles.Add(
                    baseIndex +
                    indices[i]
                );
            }
        }

        private static List<Vector2> BuildLimitedColumnPoints(
            List<Vector2> footprint
        )
        {
            List<Vector2> result =
                new List<Vector2>();

            if (
                footprint == null ||
                footprint.Count < 3
            )
            {
                return result;
            }

            const float duplicateDistance =
                0.45f;

            for (
                int i = 0;
                i < footprint.Count &&
                result.Count <
                    MaxConcreteColumnsPerFloor;
                i++
            )
            {
                AddColumnPointIfSeparated(
                    result,
                    footprint[i],
                    duplicateDistance
                );
            }

            float perimeter =
                0f;

            List<float> edgeLengths =
                new List<float>();

            for (
                int edge = 0;
                edge < footprint.Count;
                edge++
            )
            {
                float length =
                    Vector2.Distance(
                        footprint[edge],
                        footprint[
                            (
                                edge +
                                1
                            ) %
                            footprint.Count
                        ]
                    );

                edgeLengths.Add(
                    length
                );

                perimeter +=
                    length;
            }

            if (
                perimeter <
                0.01f ||
                result.Count >=
                    MaxConcreteColumnsPerFloor
            )
            {
                return result;
            }

            int desiredTotal =
                Mathf.Clamp(
                    Mathf.CeilToInt(
                        perimeter /
                        5.5f
                    ),
                    Mathf.Max(
                        3,
                        result.Count
                    ),
                    MaxConcreteColumnsPerFloor
                );

            int sampleCount =
                Mathf.Max(
                    desiredTotal *
                    4,
                    footprint.Count *
                    3
                );

            for (
                int sample = 0;
                sample < sampleCount &&
                result.Count <
                    desiredTotal;
                sample++
            )
            {
                float distance =
                    perimeter *
                    (
                        sample +
                        0.5f
                    ) /
                    sampleCount;

                Vector2 point =
                    PointAlongPolygonPerimeter(
                        footprint,
                        edgeLengths,
                        distance
                    );

                AddColumnPointIfSeparated(
                    result,
                    point,
                    duplicateDistance
                );
            }

            return result;
        }

        private static bool AddColumnPointIfSeparated(
            List<Vector2> points,
            Vector2 candidate,
            float minimumDistance
        )
        {
            if (
                points == null
            )
            {
                return false;
            }

            float minimumDistanceSquared =
                minimumDistance *
                minimumDistance;

            for (
                int i = 0;
                i < points.Count;
                i++
            )
            {
                if (
                    (
                        points[i] -
                        candidate
                    ).sqrMagnitude <
                    minimumDistanceSquared
                )
                {
                    return false;
                }
            }

            points.Add(
                candidate
            );

            return true;
        }

        private static Vector2 PointAlongPolygonPerimeter(
            List<Vector2> footprint,
            List<float> edgeLengths,
            float distance
        )
        {
            if (
                footprint == null ||
                footprint.Count == 0 ||
                edgeLengths == null ||
                edgeLengths.Count !=
                    footprint.Count
            )
            {
                return Vector2.zero;
            }

            float remaining =
                Mathf.Max(
                    0f,
                    distance
                );

            for (
                int edge = 0;
                edge < footprint.Count;
                edge++
            )
            {
                float length =
                    edgeLengths[edge];

                if (
                    length <=
                    0.0001f
                )
                {
                    continue;
                }

                if (
                    remaining <=
                    length
                )
                {
                    return
                        Vector2.Lerp(
                            footprint[edge],
                            footprint[
                                (
                                    edge +
                                    1
                                ) %
                                footprint.Count
                            ],
                            remaining /
                            length
                        );
                }

                remaining -=
                    length;
            }

            return
                footprint[
                    footprint.Count -
                    1
                ];
        }

        private Material CreateCutoffDisplayMaterial()
        {
            Shader shader =
                Shader.Find(
                    "HDRP/Lit"
                );

            if (
                shader == null
            )
            {
                shader =
                    Shader.Find(
                        "Standard"
                    );
            }

            if (
                shader == null
            )
            {
                shader =
                    Shader.Find(
                        "HDRP/Unlit"
                    );
            }

            if (
                shader == null
            )
            {
                return null;
            }

            Material material =
                new Material(
                    shader
                );

            material.name =
                "ConstructionAnimation_CutoffNeutral";

            SetMaterialColor(
                material,
                new UnityEngine.Color(
                    0.66f,
                    0.68f,
                    0.70f,
                    1f
                )
            );

            SetMaterialFloatIfPresent(
                material,
                "_Metallic",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_Smoothness",
                0.22f
            );

            SetMaterialFloatIfPresent(
                material,
                "_AlphaCutoffEnable",
                0f
            );

            ForceMaterialAlphaOne(
                material
            );

            material.SetOverrideTag(
                "RenderType",
                "Opaque"
            );

            ValidateHdrpMaterial(
                material,
                "cutoff-building"
            );

            return material;
        }

        private static CutoffVertexData IntersectCutoffVertex(
            CutoffVertexData a,
            CutoffVertexData b,
            float cutHeight
        )
        {
            float denominator =
                b.Position.y -
                a.Position.y;

            float t =
                Mathf.Abs(
                    denominator
                ) < 0.000001f
                    ? 0f
                    : Mathf.Clamp01(
                        (
                            cutHeight -
                            a.Position.y
                        ) /
                        denominator
                    );

            CutoffVertexData result =
                new CutoffVertexData();

            result.Position =
                Vector3.Lerp(
                    a.Position,
                    b.Position,
                    t
                );

            result.Position.y =
                cutHeight;

            result.Normal =
                Vector3.Lerp(
                    a.Normal,
                    b.Normal,
                    t
                );

            if (
                result.Normal.sqrMagnitude >
                0.000001f
            )
            {
                result.Normal.Normalize();
            }

            result.Tangent =
                Vector4.Lerp(
                    a.Tangent,
                    b.Tangent,
                    t
                );

            Vector3 tangentDirection =
                new Vector3(
                    result.Tangent.x,
                    result.Tangent.y,
                    result.Tangent.z
                );

            if (
                tangentDirection.sqrMagnitude >
                0.000001f
            )
            {
                tangentDirection.Normalize();

                result.Tangent.x =
                    tangentDirection.x;

                result.Tangent.y =
                    tangentDirection.y;

                result.Tangent.z =
                    tangentDirection.z;
            }

            result.UV0 =
                Vector4.Lerp(
                    a.UV0,
                    b.UV0,
                    t
                );
            result.UV1 =
                Vector4.Lerp(
                    a.UV1,
                    b.UV1,
                    t
                );
            result.UV2 =
                Vector4.Lerp(
                    a.UV2,
                    b.UV2,
                    t
                );
            result.UV3 =
                Vector4.Lerp(
                    a.UV3,
                    b.UV3,
                    t
                );
            result.UV4 =
                Vector4.Lerp(
                    a.UV4,
                    b.UV4,
                    t
                );
            result.UV5 =
                Vector4.Lerp(
                    a.UV5,
                    b.UV5,
                    t
                );
            result.UV6 =
                Vector4.Lerp(
                    a.UV6,
                    b.UV6,
                    t
                );
            result.UV7 =
                Vector4.Lerp(
                    a.UV7,
                    b.UV7,
                    t
                );

            result.Color =
                UnityEngine.Color.Lerp(
                    a.Color,
                    b.Color,
                    t
                );

            result.SourceIndex =
                -1;

            return result;
        }

        private static void AppendCutoffEdge(
            CutoffVertexData previous,
            CutoffVertexData current,
            float cutHeight,
            List<CutoffVertexData> output
        )
        {
            bool previousInside =
                previous.Position.y <=
                cutHeight +
                0.00001f;

            bool currentInside =
                current.Position.y <=
                cutHeight +
                0.00001f;

            if (
                currentInside
            )
            {
                if (
                    !previousInside
                )
                {
                    output.Add(
                        IntersectCutoffVertex(
                            previous,
                            current,
                            cutHeight
                        )
                    );
                }

                output.Add(
                    current
                );
            }
            else if (
                previousInside
            )
            {
                output.Add(
                    IntersectCutoffVertex(
                        previous,
                        current,
                        cutHeight
                    )
                );
            }
        }

        private static void ClipCutoffTriangle(
            CutoffVertexData a,
            CutoffVertexData b,
            CutoffVertexData c,
            float cutHeight,
            List<CutoffVertexData> output
        )
        {
            output.Clear();

            // Sutherland-Hodgman against one horizontal half-space.
            // Explicit edges avoid allocating a temporary 3-element array for
            // every source triangle.
            AppendCutoffEdge(
                c,
                a,
                cutHeight,
                output
            );

            AppendCutoffEdge(
                a,
                b,
                cutHeight,
                output
            );

            AppendCutoffEdge(
                b,
                c,
                cutHeight,
                output
            );
        }

        private static int AddCutoffRuntimeVertex(
            CutoffMeshVisual cutoff,
            CutoffVertexData vertex
        )
        {
            int runtimeIndex =
                cutoff.RuntimeVertices.Count;

            cutoff.RuntimeVertices.Add(
                vertex.Position
            );

            if (
                cutoff.HasNormals
            )
            {
                cutoff.RuntimeNormals.Add(
                    vertex.Normal
                );
            }

            if (
                cutoff.HasTangents
            )
            {
                cutoff.RuntimeTangents.Add(
                    vertex.Tangent
                );
            }

            if (
                cutoff.HasUVChannels[0]
            )
            {
                cutoff.RuntimeUVChannels[0].Add(
                    vertex.UV0
                );
            }

            if (
                cutoff.HasUVChannels[1]
            )
            {
                cutoff.RuntimeUVChannels[1].Add(
                    vertex.UV1
                );
            }

            if (
                cutoff.HasUVChannels[2]
            )
            {
                cutoff.RuntimeUVChannels[2].Add(
                    vertex.UV2
                );
            }

            if (
                cutoff.HasUVChannels[3]
            )
            {
                cutoff.RuntimeUVChannels[3].Add(
                    vertex.UV3
                );
            }

            if (
                cutoff.HasUVChannels[4]
            )
            {
                cutoff.RuntimeUVChannels[4].Add(
                    vertex.UV4
                );
            }

            if (
                cutoff.HasUVChannels[5]
            )
            {
                cutoff.RuntimeUVChannels[5].Add(
                    vertex.UV5
                );
            }

            if (
                cutoff.HasUVChannels[6]
            )
            {
                cutoff.RuntimeUVChannels[6].Add(
                    vertex.UV6
                );
            }

            if (
                cutoff.HasUVChannels[7]
            )
            {
                cutoff.RuntimeUVChannels[7].Add(
                    vertex.UV7
                );
            }

            if (
                cutoff.HasColors
            )
            {
                cutoff.RuntimeColors.Add(
                    vertex.Color
                );
            }

            return runtimeIndex;
        }

        private static int GetOrAddSourceCutoffVertex(
            CutoffMeshVisual cutoff,
            int sourceIndex
        )
        {
            if (
                cutoff == null ||
                sourceIndex < 0 ||
                sourceIndex >=
                    cutoff.SourceVertices.Length
            )
            {
                return -1;
            }

            int existing =
                cutoff.SourceToRuntimeIndex[
                    sourceIndex
                ];

            if (
                existing >= 0
            )
            {
                return existing;
            }

            CutoffVertexData vertex =
                CreateCutoffVertexData(
                    cutoff,
                    sourceIndex
                );

            int runtimeIndex =
                AddCutoffRuntimeVertex(
                    cutoff,
                    vertex
                );

            cutoff.SourceToRuntimeIndex[
                sourceIndex
            ] =
                runtimeIndex;

            return runtimeIndex;
        }

        private static void RebuildCutoffMesh(
            CutoffMeshVisual cutoff,
            float cutHeight
        )
        {
            if (
                cutoff == null ||
                cutoff.RuntimeMesh == null ||
                cutoff.SourceVertices == null ||
                cutoff.SourceSubMeshTriangles == null
            )
            {
                return;
            }

            List<Vector3> vertices =
                cutoff.RuntimeVertices;

            List<Vector3> normals =
                cutoff.RuntimeNormals;

            List<Vector4> tangents =
                cutoff.RuntimeTangents;

            List<UnityEngine.Color> colors =
                cutoff.RuntimeColors;

            vertices.Clear();
            normals.Clear();
            tangents.Clear();
            colors.Clear();

            for (
                int channel = 0;
                channel < cutoff.RuntimeUVChannels.Length;
                channel++
            )
            {
                cutoff.RuntimeUVChannels[
                    channel
                ].Clear();
            }

            if (
                cutoff.SourceToRuntimeIndex == null ||
                cutoff.SourceToRuntimeIndex.Length !=
                    cutoff.SourceVertices.Length
            )
            {
                cutoff.SourceToRuntimeIndex =
                    new int[
                        cutoff.SourceVertices.Length
                    ];
            }

            for (
                int i = 0;
                i < cutoff.SourceToRuntimeIndex.Length;
                i++
            )
            {
                cutoff.SourceToRuntimeIndex[i] =
                    -1;
            }

            if (
                cutoff.RuntimeSubMeshTriangles == null ||
                cutoff.RuntimeSubMeshTriangles.Length !=
                    cutoff.SourceSubMeshTriangles.Length
            )
            {
                cutoff.RuntimeSubMeshTriangles =
                    new List<int>[
                        cutoff.SourceSubMeshTriangles.Length
                    ];

                for (
                    int i = 0;
                    i < cutoff.RuntimeSubMeshTriangles.Length;
                    i++
                )
                {
                    cutoff.RuntimeSubMeshTriangles[i] =
                        new List<int>();
                }
            }

            for (
                int i = 0;
                i < cutoff.RuntimeSubMeshTriangles.Length;
                i++
            )
            {
                cutoff.RuntimeSubMeshTriangles[i].Clear();
            }

            List<CutoffVertexData> clipped =
                cutoff.RuntimeClipped;

            List<int> polygonIndices =
                cutoff.RuntimePolygonIndices;

            for (
                int subMeshIndex = 0;
                subMeshIndex < cutoff.SourceSubMeshTriangles.Length;
                subMeshIndex++
            )
            {
                int[] sourceTriangles =
                    cutoff.SourceSubMeshTriangles[
                        subMeshIndex
                    ];

                if (
                    sourceTriangles == null ||
                    sourceTriangles.Length < 3
                )
                {
                    continue;
                }

                List<int> triangles =
                    cutoff.RuntimeSubMeshTriangles[
                        subMeshIndex
                    ];

                for (
                    int triangleIndex = 0;
                    triangleIndex + 2 <
                    sourceTriangles.Length;
                    triangleIndex += 3
                )
                {
                    int index0 =
                        sourceTriangles[
                            triangleIndex
                        ];

                    int index1 =
                        sourceTriangles[
                            triangleIndex +
                            1
                        ];

                    int index2 =
                        sourceTriangles[
                            triangleIndex +
                            2
                        ];

                    if (
                        index0 < 0 ||
                        index1 < 0 ||
                        index2 < 0 ||
                        index0 >= cutoff.SourceVertices.Length ||
                        index1 >= cutoff.SourceVertices.Length ||
                        index2 >= cutoff.SourceVertices.Length
                    )
                    {
                        continue;
                    }

                    const float epsilon =
                        0.00001f;

                    bool inside0 =
                        cutoff.SourceVertices[
                            index0
                        ].y <=
                        cutHeight +
                        epsilon;

                    bool inside1 =
                        cutoff.SourceVertices[
                            index1
                        ].y <=
                        cutHeight +
                        epsilon;

                    bool inside2 =
                        cutoff.SourceVertices[
                            index2
                        ].y <=
                        cutHeight +
                        epsilon;

                    if (
                        inside0 &&
                        inside1 &&
                        inside2
                    )
                    {
                        int runtime0 =
                            GetOrAddSourceCutoffVertex(
                                cutoff,
                                index0
                            );

                        int runtime1 =
                            GetOrAddSourceCutoffVertex(
                                cutoff,
                                index1
                            );

                        int runtime2 =
                            GetOrAddSourceCutoffVertex(
                                cutoff,
                                index2
                            );

                        if (
                            runtime0 >= 0 &&
                            runtime1 >= 0 &&
                            runtime2 >= 0
                        )
                        {
                            triangles.Add(
                                runtime0
                            );

                            triangles.Add(
                                runtime1
                            );

                            triangles.Add(
                                runtime2
                            );
                        }

                        continue;
                    }

                    if (
                        !inside0 &&
                        !inside1 &&
                        !inside2
                    )
                    {
                        continue;
                    }

                    CutoffVertexData a =
                        CreateCutoffVertexData(
                            cutoff,
                            index0
                        );

                    CutoffVertexData b =
                        CreateCutoffVertexData(
                            cutoff,
                            index1
                        );

                    CutoffVertexData c =
                        CreateCutoffVertexData(
                            cutoff,
                            index2
                        );

                    ClipCutoffTriangle(
                        a,
                        b,
                        c,
                        cutHeight,
                        clipped
                    );

                    if (
                        clipped.Count < 3
                    )
                    {
                        continue;
                    }

                    polygonIndices.Clear();

                    for (
                        int i = 0;
                        i < clipped.Count;
                        i++
                    )
                    {
                        CutoffVertexData vertex =
                            clipped[i];

                        int runtimeIndex =
                            vertex.SourceIndex >= 0
                                ? GetOrAddSourceCutoffVertex(
                                    cutoff,
                                    vertex.SourceIndex
                                )
                                : AddCutoffRuntimeVertex(
                                    cutoff,
                                    vertex
                                );

                        if (
                            runtimeIndex >= 0
                        )
                        {
                            polygonIndices.Add(
                                runtimeIndex
                            );
                        }
                    }

                    if (
                        polygonIndices.Count < 3
                    )
                    {
                        continue;
                    }

                    for (
                        int fanIndex = 1;
                        fanIndex + 1 <
                        polygonIndices.Count;
                        fanIndex++
                    )
                    {
                        triangles.Add(
                            polygonIndices[0]
                        );

                        triangles.Add(
                            polygonIndices[
                                fanIndex
                            ]
                        );

                        triangles.Add(
                            polygonIndices[
                                fanIndex +
                                1
                            ]
                        );
                    }
                }
            }

            Mesh mesh =
                cutoff.RuntimeMesh;

            mesh.Clear();

            mesh.indexFormat =
                UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(
                vertices
            );

            mesh.subMeshCount =
                Mathf.Max(
                    1,
                    cutoff.RuntimeSubMeshTriangles.Length
                );

            for (
                int subMeshIndex = 0;
                subMeshIndex < cutoff.RuntimeSubMeshTriangles.Length;
                subMeshIndex++
            )
            {
                mesh.SetTriangles(
                    cutoff.RuntimeSubMeshTriangles[
                        subMeshIndex
                    ],
                    subMeshIndex,
                    false
                );
            }

            if (
                cutoff.HasNormals &&
                normals.Count == vertices.Count
            )
            {
                mesh.SetNormals(
                    normals
                );
            }
            else if (
                vertices.Count > 0
            )
            {
                mesh.RecalculateNormals();
            }

            if (
                cutoff.HasTangents &&
                tangents.Count == vertices.Count
            )
            {
                mesh.SetTangents(
                    tangents
                );
            }

            for (
                int channel = 0;
                channel < 8;
                channel++
            )
            {
                if (
                    cutoff.HasUVChannels[
                        channel
                    ] &&
                    cutoff.RuntimeUVChannels[
                        channel
                    ].Count ==
                    vertices.Count
                )
                {
                    mesh.SetUVs(
                        channel,
                        cutoff.RuntimeUVChannels[
                            channel
                        ]
                    );
                }
            }

            if (
                cutoff.HasColors &&
                colors.Count == vertices.Count
            )
            {
                mesh.SetColors(
                    colors
                );
            }

            mesh.RecalculateBounds();
        }

        private static Vector4 GetCutoffSourceUV(
            CutoffMeshVisual cutoff,
            int channel,
            int index
        )
        {
            if (
                cutoff == null ||
                channel < 0 ||
                channel >= 8 ||
                cutoff.SourceUVChannels == null ||
                channel >= cutoff.SourceUVChannels.Length ||
                cutoff.SourceUVChannels[
                    channel
                ] == null ||
                index < 0 ||
                index >=
                    cutoff.SourceUVChannels[
                        channel
                    ].Length
            )
            {
                return Vector4.zero;
            }

            return
                cutoff.SourceUVChannels[
                    channel
                ][index];
        }

        private static CutoffVertexData CreateCutoffVertexData(
            CutoffMeshVisual cutoff,
            int index
        )
        {
            CutoffVertexData result =
                new CutoffVertexData();

            result.Position =
                cutoff.SourceVertices[index];

            result.Normal =
                cutoff.HasNormals
                    ? cutoff.SourceNormals[index]
                    : Vector3.up;

            result.Tangent =
                cutoff.HasTangents
                    ? cutoff.SourceTangents[index]
                    : new Vector4(
                        1f,
                        0f,
                        0f,
                        1f
                    );

            result.UV0 =
                GetCutoffSourceUV(
                    cutoff,
                    0,
                    index
                );
            result.UV1 =
                GetCutoffSourceUV(
                    cutoff,
                    1,
                    index
                );
            result.UV2 =
                GetCutoffSourceUV(
                    cutoff,
                    2,
                    index
                );
            result.UV3 =
                GetCutoffSourceUV(
                    cutoff,
                    3,
                    index
                );
            result.UV4 =
                GetCutoffSourceUV(
                    cutoff,
                    4,
                    index
                );
            result.UV5 =
                GetCutoffSourceUV(
                    cutoff,
                    5,
                    index
                );
            result.UV6 =
                GetCutoffSourceUV(
                    cutoff,
                    6,
                    index
                );
            result.UV7 =
                GetCutoffSourceUV(
                    cutoff,
                    7,
                    index
                );

            result.Color =
                cutoff.HasColors
                    ? cutoff.SourceColors[index]
                    : UnityEngine.Color.white;

            result.SourceIndex =
                index;

            return result;
        }

        private void CreateCutoffBuildingVisual(
            ConstructionVisual visual,
            Entity buildingPrefab
        )
        {
            if (
                visual == null ||
                buildingPrefab == Entity.Null ||
                !EntityManager.Exists(
                    buildingPrefab
                )
            )
            {
                return;
            }

            GameObject root =
                new GameObject(
                    "ConstructionAnimation_CutoffBuilding_" +
                    visual.Source.Index +
                    "_" +
                    visual.Source.Version
                );

            root.hideFlags =
                HideFlags.DontSave;

            visual.BuildingVisualRoot =
                root;

            visual.BuildingFoldRoot =
                root;

            visual.ConcreteStructureRoot =
                null;

            visual.RoofStructureRoot =
                null;

            Material cutoffMaterial =
                CreateCutoffDisplayMaterial();

            if (
                cutoffMaterial != null
            )
            {
                visual.BuildingVisualMaterials.Add(
                    cutoffMaterial
                );
            }

            float globalMinY =
                float.MaxValue;

            float globalMaxY =
                float.MinValue;

            int totalVertices =
                0;

            int totalTriangles =
                0;

            int createdMeshes =
                0;

            if (
                !EntityManager.HasBuffer<SubMesh>(
                    buildingPrefab
                )
            )
            {
                ModLog.Info(
                    "CUTOFF-MESH skip; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; reason=no SubMesh buffer"
                );

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
                    subMeshes[subIndex];

                Entity renderPrefabEntity =
                    subMesh.m_SubMesh;

                if (
                    renderPrefabEntity == Entity.Null ||
                    !EntityManager.Exists(
                        renderPrefabEntity
                    )
                )
                {
                    ModLog.Info(
                        "CUTOFF-MESH skip; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; sub=" +
                        subIndex +
                        "; reason=render prefab missing"
                    );

                    continue;
                }

                try
                {
                    PrefabBase managedPrefab =
                        m_PrefabSystem.GetPrefab<PrefabBase>(
                            renderPrefabEntity
                        );

                    GeometryAsset geometryAsset =
                        GetGeometryAsset(
                            managedPrefab
                        );

                    if (
                        geometryAsset == null
                    )
                    {
                        ModLog.Info(
                            "CUTOFF-MESH skip; source=" +
                            visual.Source.Index +
                            ":" +
                            visual.Source.Version +
                            "; sub=" +
                            subIndex +
                            "; reason=no GeometryAsset"
                        );

                        continue;
                    }

                    Mesh[] meshes =
                        geometryAsset.ObtainMeshes(
                            true
                        );

                    // V1.43.47.4.3.5 diagnostic: vanilla RenderPrefabRenderer iterates
                    // every mesh returned by RenderPrefab.ObtainMeshes(), consuming the
                    // material array sequentially across all mesh submeshes. Our cutoff
                    // renderer currently uses only meshes[0]. Log the exact topology so
                    // we can verify whether window geometry lives in a later mesh.
                    try
                    {
                        RenderPrefab diagnosticRenderPrefab =
                            managedPrefab as RenderPrefab;

                        int diagnosticTotalSubMeshes =
                            0;

                        if (meshes != null)
                        {
                            for (
                                int diagnosticMeshIndex = 0;
                                diagnosticMeshIndex < meshes.Length;
                                diagnosticMeshIndex++
                            )
                            {
                                Mesh diagnosticMesh =
                                    meshes[diagnosticMeshIndex];

                                if (diagnosticMesh == null)
                                {
                                    ModLog.Checkpoint(
                                        "CUTOFF-MESH-LAYOUT; renderPrefab=" +
                                        renderPrefabEntity.Index +
                                        ":" +
                                        renderPrefabEntity.Version +
                                        "; mesh=" +
                                        diagnosticMeshIndex +
                                        "; null=True"
                                    );

                                    continue;
                                }

                                diagnosticTotalSubMeshes +=
                                    diagnosticMesh.subMeshCount;

                                ModLog.Checkpoint(
                                    "CUTOFF-MESH-LAYOUT; renderPrefab=" +
                                    renderPrefabEntity.Index +
                                    ":" +
                                    renderPrefabEntity.Version +
                                    "; mesh=" +
                                    diagnosticMeshIndex +
                                    "; name=" +
                                    diagnosticMesh.name +
                                    "; vertices=" +
                                    diagnosticMesh.vertexCount +
                                    "; subMeshes=" +
                                    diagnosticMesh.subMeshCount +
                                    "; triangles=" +
                                    (diagnosticMesh.triangles != null
                                        ? diagnosticMesh.triangles.Length / 3
                                        : 0)
                                );
                            }
                        }

                        ModLog.Checkpoint(
                            "CUTOFF-MESH-LAYOUT summary; renderPrefab=" +
                            renderPrefabEntity.Index +
                            ":" +
                            renderPrefabEntity.Version +
                            "; meshCount=" +
                            (meshes != null ? meshes.Length : 0) +
                            "; totalSubMeshes=" +
                            diagnosticTotalSubMeshes +
                            "; materialCount=" +
                            (diagnosticRenderPrefab != null
                                ? diagnosticRenderPrefab.materialCount
                                : -1) +
                            "; currentRendererUsesOnlyMesh0=True"
                        );
                    }
                    catch (Exception diagnosticEx)
                    {
                        ModLog.Info(
                            "V1.43.47.4.3.5 mesh-layout diagnostic failed; renderPrefab=" +
                            renderPrefabEntity.Index +
                            ":" +
                            renderPrefabEntity.Version +
                            "; error=" +
                            diagnosticEx.GetType().Name +
                            ": " +
                            diagnosticEx.Message
                        );
                    }

                    if (
                        meshes == null ||
                        meshes.Length == 0 ||
                        meshes[0] == null
                    )
                    {
                        ModLog.Info(
                            "CUTOFF-MESH skip; source=" +
                            visual.Source.Index +
                            ":" +
                            visual.Source.Version +
                            "; sub=" +
                            subIndex +
                            "; reason=no readable mesh"
                        );

                        continue;
                    }

                    int materialStartIndex =
                        0;

                    for (
                        int meshIndex = 0;
                        meshIndex < meshes.Length;
                        meshIndex++
                    )
                    {
                        if (
                            meshes[meshIndex] == null
                        )
                        {
                            continue;
                        }

                        int sourceSubMeshCountForOffset =
                            Mathf.Max(
                                1,
                                meshes[meshIndex].subMeshCount
                            );

                        int currentMaterialStartIndex =
                            materialStartIndex;

                        materialStartIndex +=
                            sourceSubMeshCountForOffset;

                            Mesh sourceMesh =
                                meshes[meshIndex];

                        Vector3[] sourceVertices =
                            sourceMesh.vertices;

                        int sourceSubMeshCount =
                            Mathf.Max(
                                1,
                                sourceMesh.subMeshCount
                            );

                        int[][] sourceSubMeshTriangles =
                            new int[
                                sourceSubMeshCount
                            ][];

                        int sourceTriangleCount =
                            0;

                        for (
                            int materialIndex = 0;
                            materialIndex < sourceSubMeshCount;
                            materialIndex++
                        )
                        {
                            int[] materialTriangles =
                                sourceMesh.subMeshCount > 0
                                    ? sourceMesh.GetTriangles(
                                        materialIndex
                                    )
                                    : sourceMesh.triangles;

                            sourceSubMeshTriangles[
                                materialIndex
                            ] =
                                materialTriangles ??
                                new int[0];

                            sourceTriangleCount +=
                                sourceSubMeshTriangles[
                                    materialIndex
                                ].Length /
                                3;
                        }

                        if (
                            sourceVertices == null ||
                            sourceVertices.Length < 3 ||
                            sourceTriangleCount <= 0
                        )
                        {
                            ModLog.Info(
                                "CUTOFF-MESH skip; source=" +
                                visual.Source.Index +
                                ":" +
                                visual.Source.Version +
                                "; sub=" +
                                subIndex +
                                "; reason=empty geometry"
                            );

                            continue;
                        }

                        Vector3[] transformedVertices =
                            new Vector3[
                                sourceVertices.Length
                            ];

                        Vector3[] sourceNormals =
                            sourceMesh.normals;

                        bool hasNormals =
                            sourceNormals != null &&
                            sourceNormals.Length ==
                            sourceVertices.Length;

                        Vector3[] transformedNormals =
                            hasNormals
                                ? new Vector3[
                                    sourceVertices.Length
                                ]
                                : new Vector3[0];

                        Vector4[] sourceTangents =
                            sourceMesh.tangents;

                        bool hasTangents =
                            sourceTangents != null &&
                            sourceTangents.Length ==
                            sourceVertices.Length;

                        Vector4[] transformedTangents =
                            hasTangents
                                ? new Vector4[
                                    sourceVertices.Length
                                ]
                                : new Vector4[0];

                        Vector4[][] sourceUVChannels =
                            new Vector4[8][];

                        bool[] hasUVChannels =
                            new bool[8];

                        for (
                            int uvChannel = 0;
                            uvChannel < 8;
                            uvChannel++
                        )
                        {
                            List<Vector4> uvValues =
                                new List<Vector4>();

                            sourceMesh.GetUVs(
                                uvChannel,
                                uvValues
                            );

                            if (
                                uvValues.Count ==
                                sourceVertices.Length
                            )
                            {
                                sourceUVChannels[
                                    uvChannel
                                ] =
                                    uvValues.ToArray();

                                hasUVChannels[
                                    uvChannel
                                ] =
                                    true;
                            }
                            else
                            {
                                sourceUVChannels[
                                    uvChannel
                                ] =
                                    new Vector4[0];

                                hasUVChannels[
                                    uvChannel
                                ] =
                                    false;
                            }
                        }

                        UnityEngine.Color[] sourceColors =
                            sourceMesh.colors;

                        bool hasColors =
                            sourceColors != null &&
                            sourceColors.Length ==
                            sourceVertices.Length;

                        float localMinY =
                            float.MaxValue;

                        float localMaxY =
                            float.MinValue;

                        for (
                            int vertexIndex = 0;
                            vertexIndex < sourceVertices.Length;
                            vertexIndex++
                        )
                        {
                            Vector3 sourceVertex =
                                sourceVertices[
                                    vertexIndex
                                ];

                            float3 local =
                                math.rotate(
                                    subMesh.m_Rotation,
                                    new float3(
                                        sourceVertex.x,
                                        sourceVertex.y,
                                        sourceVertex.z
                                    )
                                );

                            local +=
                                subMesh.m_Position;

                            transformedVertices[
                                vertexIndex
                            ] =
                                new Vector3(
                                    local.x,
                                    local.y,
                                    local.z
                                );

                            localMinY =
                                Mathf.Min(
                                    localMinY,
                                    local.y
                                );

                            localMaxY =
                                Mathf.Max(
                                    localMaxY,
                                    local.y
                                );

                            if (
                                hasNormals
                            )
                            {
                                Vector3 normal =
                                    sourceNormals[
                                        vertexIndex
                                    ];

                                float3 rotatedNormal =
                                    math.rotate(
                                        subMesh.m_Rotation,
                                        new float3(
                                            normal.x,
                                            normal.y,
                                            normal.z
                                        )
                                    );

                                transformedNormals[
                                    vertexIndex
                                ] =
                                    new Vector3(
                                        rotatedNormal.x,
                                        rotatedNormal.y,
                                        rotatedNormal.z
                                    ).normalized;
                            }

                            if (
                                hasTangents
                            )
                            {
                                Vector4 tangent =
                                    sourceTangents[
                                        vertexIndex
                                    ];

                                float3 rotatedTangent =
                                    math.rotate(
                                        subMesh.m_Rotation,
                                        new float3(
                                            tangent.x,
                                            tangent.y,
                                            tangent.z
                                        )
                                    );

                                transformedTangents[
                                    vertexIndex
                                ] =
                                    new Vector4(
                                        rotatedTangent.x,
                                        rotatedTangent.y,
                                        rotatedTangent.z,
                                        tangent.w
                                    );
                            }
                        }

                        if (
                            localMinY == float.MaxValue ||
                            localMaxY == float.MinValue
                        )
                        {
                            continue;
                        }

                        GameObject child =
                            new GameObject(
                                    "CutoffMesh_" +
                                    subIndex +
                                    "_" +
                                    meshIndex
                            );

                        child.hideFlags =
                            HideFlags.DontSave;

                        child.transform.SetParent(
                            root.transform,
                            false
                        );

                        Mesh runtimeMesh =
                            new Mesh();

                        runtimeMesh.name =
                                "ConstructionAnimation_CutoffMesh_" +
                                visual.Source.Index +
                                "_" +
                                subIndex +
                                "_" +
                                meshIndex;

                        runtimeMesh.indexFormat =
                            UnityEngine.Rendering.IndexFormat.UInt32;

                        MeshFilter filter =
                            child.AddComponent<MeshFilter>();

                        filter.sharedMesh =
                            runtimeMesh;

                        MeshRenderer renderer =
                            child.AddComponent<MeshRenderer>();

                        Material[] sourceMaterials =
                                TryCreateFoldedMaterialsFromSurfaceAssets(
                                    visual,
                                    managedPrefab,
                                    renderPrefabEntity,
                                    sourceSubMeshCount,
                                    currentMaterialStartIndex
                                );

                        if (
                            sourceMaterials == null ||
                            sourceMaterials.Length == 0
                        )
                        {
                            sourceMaterials =
                                new Material[
                                    sourceSubMeshCount
                                ];

                            for (
                                int materialIndex = 0;
                                materialIndex < sourceMaterials.Length;
                                materialIndex++
                            )
                            {
                                sourceMaterials[
                                    materialIndex
                                ] =
                                    cutoffMaterial;
                            }
                        }

                        renderer.sharedMaterials =
                            sourceMaterials;

                        ConfigureStructureRenderer(
                            renderer
                        );

                        CutoffMeshVisual cutoff =
                            new CutoffMeshVisual();

                        cutoff.Root =
                            child;

                        cutoff.RuntimeMesh =
                            runtimeMesh;

                        cutoff.SourceVertices =
                            transformedVertices;

                        cutoff.SourceNormals =
                            transformedNormals;

                        cutoff.SourceTangents =
                            transformedTangents;

                        cutoff.SourceUVChannels =
                            sourceUVChannels;

                        for (
                            int uvChannel = 0;
                            uvChannel < 8;
                            uvChannel++
                        )
                        {
                            cutoff.HasUVChannels[
                                uvChannel
                            ] =
                                hasUVChannels[
                                    uvChannel
                                ];
                        }

                        cutoff.SourceColors =
                            hasColors
                                ? sourceColors
                                : new UnityEngine.Color[0];

                        cutoff.SourceSubMeshTriangles =
                            sourceSubMeshTriangles;

                        cutoff.RuntimeSubMeshTriangles =
                            new List<int>[
                                sourceSubMeshCount
                            ];

                        for (
                            int materialIndex = 0;
                            materialIndex < sourceSubMeshCount;
                            materialIndex++
                        )
                        {
                            cutoff.RuntimeSubMeshTriangles[
                                materialIndex
                            ] =
                                new List<int>();
                        }

                        cutoff.HasNormals =
                            hasNormals;

                        cutoff.HasTangents =
                            hasTangents;

                        cutoff.HasColors =
                            hasColors;

                        cutoff.MinY =
                            localMinY;

                        cutoff.MaxY =
                            localMaxY;

                        cutoff.SourceSubMeshIndex =
                            subIndex;

                        cutoff.SourcePrefabName =
                            managedPrefab.GetType().Name;

                        visual.CutoffMeshes.Add(
                            cutoff
                        );

                        visual.BuildingVisualMeshes.Add(
                            runtimeMesh
                        );

                        globalMinY =
                            Mathf.Min(
                                globalMinY,
                                localMinY
                            );

                        globalMaxY =
                            Mathf.Max(
                                globalMaxY,
                                localMaxY
                            );

                        totalVertices +=
                            sourceVertices.Length;

                        totalTriangles +=
                            sourceTriangleCount;

                        createdMeshes++;

                        ModLog.Info(
                            "CUTOFF-MESH source=" +
                            visual.Source.Index +
                            ":" +
                            visual.Source.Version +
                            "; sub=" +
                            subIndex +
                                "; mesh=" +
                                meshIndex +
                                "; prefab=" +
                                managedPrefab.GetType().Name +
                            "; vertices=" +
                            sourceVertices.Length +
                            "; triangles=" +
                            sourceTriangleCount +
                            "; materials=" +
                            sourceSubMeshCount +
                            "; minY=" +
                            localMinY.ToString("0.00") +
                            "; maxY=" +
                            localMaxY.ToString("0.00")
                        );
                    }
                }
                catch (
                    Exception ex
                )
                {
                    ModLog.Info(
                        "CUTOFF-MESH skip; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; sub=" +
                        subIndex +
                        "; reason=" +
                        ex.GetType().Name +
                        ": " +
                        ex.Message
                    );
                }
            }

            if (
                createdMeshes > 0
            )
            {
                ApplySourceMeshColorToFoldedBuilding(
                    visual
                );

                ApplyBuildingStateToFoldedBuilding(
                    visual
                );
            }

            if (
                createdMeshes <= 0 ||
                globalMinY == float.MaxValue ||
                globalMaxY == float.MinValue
            )
            {
                visual.CutoffLocalMinY =
                    0f;

                visual.CutoffLocalMaxY =
                    Mathf.Max(
                        0.1f,
                        visual.BuildingHeight
                    );

                visual.HasCutoffHeight =
                    false;

                ModLog.Info(
                    "CUTOFF-CREATE complete source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; meshes=0; status=no usable geometry"
                );

                return;
            }

            visual.CutoffLocalMinY =
                globalMinY;

            visual.CutoffLocalMaxY =
                Mathf.Max(
                    globalMinY +
                    0.1f,
                    globalMaxY
                );

            visual.BuildingVisualBaseY =
                globalMinY;

            for (
                int i = 0;
                i < visual.CutoffMeshes.Count;
                i++
            )
            {
                RebuildCutoffMesh(
                    visual.CutoffMeshes[i],
                    visual.CutoffLocalMinY
                );
            }

            visual.LastCutoffHeight =
                visual.CutoffLocalMinY;

            visual.HasCutoffHeight =
                true;

            visual.LastCutoffLoggedProgress =
                0f;

            ModLog.Checkpoint(
                "CUTOFF-RENDER source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; renderers=" +
                createdMeshes +
                "; sourceVertices=" +
                totalVertices +
                "; sourceTriangles=" +
                totalTriangles +
                "; note=one renderer per ECS render submesh; camera-stutter diagnostic"
            );

            ModLog.Info(
                "CUTOFF-CREATE complete source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; meshes=" +
                createdMeshes +
                "; vertices=" +
                totalVertices +
                "; triangles=" +
                totalTriangles +
                "; minY=" +
                visual.CutoffLocalMinY.ToString("0.00") +
                "; maxY=" +
                visual.CutoffLocalMaxY.ToString("0.00")
            );
        }

        private void SetCompletedAssetFade(
            ConstructionVisual visual,
            float visibility
        )
        {
            if (
                visual == null ||
                visual.BuildingVisualRoot == null
            )
            {
                return;
            }

            float clampedVisibility =
                Mathf.Clamp01(
                    visibility
                );

            MeshRenderer[] renderers =
                visual.BuildingVisualRoot
                    .GetComponentsInChildren<MeshRenderer>(
                        true
                    );

            for (
                int i = 0;
                i < renderers.Length;
                i++
            )
            {
                MeshRenderer renderer =
                    renderers[i];

                if (
                    renderer == null
                )
                {
                    continue;
                }

                MaterialPropertyBlock block =
                    new MaterialPropertyBlock();

                renderer.GetPropertyBlock(
                    block
                );

                block.SetFloat(
                    "colossal_LodFade",
                    clampedVisibility
                );

                renderer.SetPropertyBlock(
                    block
                );
            }


        }

        private void UpdateCutoffBuildingVisual(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            float visualProgress
        )
        {
            if (
                visual == null ||
                visual.BuildingVisualRoot == null
            )
            {
                return;
            }

            float progress =
                Mathf.Clamp01(
                    visualProgress
                );

            float3 position =
                sourceTransform.m_Position;

            visual.BuildingVisualRoot.transform.position =
                new Vector3(
                    position.x,
                    position.y,
                    position.z
                );

            quaternion q =
                sourceTransform.m_Rotation;

            visual.BuildingVisualRoot.transform.rotation =
                new Quaternion(
                    q.value.x,
                    q.value.y,
                    q.value.z,
                    q.value.w
                );

            if (
                visual.CutoffMeshes == null ||
                visual.CutoffMeshes.Count == 0
            )
            {
                return;
            }

            float cutHeight =
                Mathf.Lerp(
                    visual.CutoffLocalMinY,
                    visual.CutoffLocalMaxY,
                    progress
                );

            float heightDelta =
                Mathf.Abs(
                    cutHeight -
                    visual.LastCutoffHeight
                );

            bool endpoint =
                progress <= 0.001f ||
                progress >= 0.999f;

            float adaptiveCutoffThreshold =
                Mathf.Clamp(
                    visual.BuildingHeight /
                    Mathf.Max(
                        CutoffTargetVerticalSteps,
                        1f
                    ),
                    CutoffHeightUpdateMinimumThreshold,
                    CutoffHeightUpdateMaximumThreshold
                );

            bool mustUpdate =
                !visual.HasCutoffHeight ||
                heightDelta >=
                    adaptiveCutoffThreshold ||
                (
                    endpoint &&
                    heightDelta > 0.001f
                );

            if (
                !mustUpdate
            )
            {
                return;
            }

            for (
                int i = 0;
                i < visual.CutoffMeshes.Count;
                i++
            )
            {
                RebuildCutoffMesh(
                    visual.CutoffMeshes[i],
                    cutHeight
                );
            }

            visual.LastCutoffHeight =
                cutHeight;

            visual.HasCutoffHeight =
                true;

            if (
                visual.LastCutoffLoggedProgress < 0f ||
                Mathf.Abs(
                    progress -
                    visual.LastCutoffLoggedProgress
                ) >= 0.10f ||
                endpoint
            )
            {
                int runtimeVertexCount =
                    0;

                int sourceVertexCount =
                    0;

                for (
                    int i = 0;
                    i < visual.CutoffMeshes.Count;
                    i++
                )
                {
                    CutoffMeshVisual cutoff =
                        visual.CutoffMeshes[i];

                    if (
                        cutoff == null
                    )
                    {
                        continue;
                    }

                    runtimeVertexCount +=
                        cutoff.RuntimeVertices.Count;

                    sourceVertexCount +=
                        cutoff.SourceVertices != null
                            ? cutoff.SourceVertices.Length
                            : 0;
                }

                ModLog.Checkpoint(
                    "CUTOFF-UPDATE source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; progress=" +
                    progress.ToString("0.000") +
                    "; height=" +
                    cutHeight.ToString("0.00") +
                    "; meshes=" +
                    visual.CutoffMeshes.Count +
                    "; runtimeVertices=" +
                    runtimeVertexCount +
                    "; sourceVertices=" +
                    sourceVertexCount
                );

                visual.LastCutoffLoggedProgress =
                    progress;
            }
        }

        private void CreateFoldedBuildingVisual(
            ConstructionVisual visual,
            Entity buildingPrefab
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            DestroyFoldedBuildingVisual(
                visual
            );

            CreateCutoffBuildingVisual(
                visual,
                buildingPrefab
            );
        }

        private GameObject CreateConcreteRoofStructure(
            GameObject parent,
            ConstructionVisual visual,
            float structuralTop
        )
        {
            if (
                parent == null ||
                visual == null ||
                visual.StructureTriangleVertices == null ||
                visual.StructureTriangleVertices.Count < 3
            )
            {
                return null;
            }

            float roofBaseY =
                visual.StructureGeometryBaseY +
                structuralTop;

            float footprintArea =
                visual.Footprint != null &&
                visual.Footprint.Count >= 3
                    ? Mathf.Abs(
                        SignedPolygonArea(
                            visual.Footprint
                        )
                    )
                    : 0f;

            float minimumTriangleProjectedArea =
                Mathf.Clamp(
                    footprintArea *
                    0.00035f,
                    0.05f,
                    0.30f
                );

            List<RoofTriangleCandidate> candidates =
                new List<RoofTriangleCandidate>();

            float highestCentroidY =
                roofBaseY;

            bool hasSlopedRoof =
                false;

            int rejectedBelow =
                0;

            int rejectedVertical =
                0;

            int rejectedTiny =
                0;

            for (
                int triangleIndex = 0;
                triangleIndex + 2 < visual.StructureTriangleVertices.Count;
                triangleIndex += 3
            )
            {
                Vector3 a =
                    visual.StructureTriangleVertices[
                        triangleIndex
                    ];

                Vector3 b =
                    visual.StructureTriangleVertices[
                        triangleIndex +
                        1
                    ];

                Vector3 c =
                    visual.StructureTriangleVertices[
                        triangleIndex +
                        2
                    ];

                float maximumY =
                    Mathf.Max(
                        a.y,
                        Mathf.Max(
                            b.y,
                            c.y
                        )
                    );

                if (
                    maximumY <
                    roofBaseY +
                    0.08f
                )
                {
                    rejectedBelow++;

                    continue;
                }

                Vector3 normal =
                    Vector3.Cross(
                        b -
                        a,
                        c -
                        a
                    );

                float normalMagnitude =
                    normal.magnitude;

                if (
                    normalMagnitude <
                    0.0001f
                )
                {
                    rejectedTiny++;

                    continue;
                }

                normal /=
                    normalMagnitude;

                if (
                    normal.y <
                    0f
                )
                {
                    normal =
                        -normal;
                }

                // Gables and upper walls are not roof planes.
                if (
                    normal.y <
                    0.22f
                )
                {
                    rejectedVertical++;

                    continue;
                }

                Vector2 a2 =
                    new Vector2(
                        a.x,
                        a.z
                    );

                Vector2 b2 =
                    new Vector2(
                        b.x,
                        b.z
                    );

                Vector2 c2 =
                    new Vector2(
                        c.x,
                        c.z
                    );

                float projectedArea =
                    Mathf.Abs(
                        Cross2D(
                            b2 -
                            a2,
                            c2 -
                            a2
                        )
                    ) *
                    0.5f;

                if (
                    projectedArea <
                    minimumTriangleProjectedArea
                )
                {
                    rejectedTiny++;

                    continue;
                }

                float centroidY =
                    (
                        a.y +
                        b.y +
                        c.y
                    ) /
                    3f;

                Vector3 centroid =
                    (
                        a +
                        b +
                        c
                    ) /
                    3f;

                RoofTriangleCandidate candidate =
                    new RoofTriangleCandidate
                    {
                        A =
                            a,
                        B =
                            b,
                        C =
                            c,
                        Normal =
                            normal,
                        ProjectedArea =
                            projectedArea,
                        CentroidY =
                            centroidY,
                        PlaneDistance =
                            Vector3.Dot(
                                normal,
                                centroid
                            )
                    };

                candidates.Add(
                    candidate
                );

                highestCentroidY =
                    Mathf.Max(
                        highestCentroidY,
                        centroidY
                    );

                if (
                    normal.y <
                    0.985f
                )
                {
                    hasSlopedRoof =
                        true;
                }
            }

            if (
                candidates.Count == 0
            )
            {
                ModLog.Checkpoint(
                    "STRUCTURE-ROOF skipped; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; reason=no-candidates"
                );

                return null;
            }

            Dictionary<string, RoofPlaneGroup> groups =
                new Dictionary<string, RoofPlaneGroup>();

            for (
                int i = 0;
                i < candidates.Count;
                i++
            )
            {
                RoofTriangleCandidate candidate =
                    candidates[i];

                // If the asset contains genuine slopes, horizontal slabs
                // above the last storey are excluded completely.
                if (
                    hasSlopedRoof &&
                    candidate.Normal.y >=
                        0.985f
                )
                {
                    continue;
                }

                // Flat roofs use only the uppermost horizontal cap, not
                // lower floor slabs that happen to sit above structuralTop.
                if (
                    !hasSlopedRoof &&
                    candidate.CentroidY <
                        highestCentroidY -
                        0.65f
                )
                {
                    continue;
                }

                const float normalQuantization =
                    0.06f;

                const float planeQuantization =
                    0.30f;

                int nx =
                    Mathf.RoundToInt(
                        candidate.Normal.x /
                        normalQuantization
                    );

                int ny =
                    Mathf.RoundToInt(
                        candidate.Normal.y /
                        normalQuantization
                    );

                int nz =
                    Mathf.RoundToInt(
                        candidate.Normal.z /
                        normalQuantization
                    );

                int pd =
                    Mathf.RoundToInt(
                        candidate.PlaneDistance /
                        planeQuantization
                    );

                string key =
                    nx +
                    ":" +
                    ny +
                    ":" +
                    nz +
                    ":" +
                    pd;

                RoofPlaneGroup group =
                    null;

                if (
                    !groups.TryGetValue(
                        key,
                        out group
                    )
                )
                {
                    group =
                        new RoofPlaneGroup
                        {
                            Key =
                                key,
                            Normal =
                                candidate.Normal,
                            ProjectedArea =
                                0f,
                            MaximumCentroidY =
                                candidate.CentroidY
                        };

                    groups.Add(
                        key,
                        group
                    );
                }

                group.Triangles.Add(
                    candidate
                );

                group.ProjectedArea +=
                    candidate.ProjectedArea;

                group.MaximumCentroidY =
                    Mathf.Max(
                        group.MaximumCentroidY,
                        candidate.CentroidY
                    );
            }

            float minimumPlaneArea =
                Mathf.Max(
                    1.25f,
                    footprintArea *
                    0.055f
                );

            List<RoofPlaneGroup> acceptedGroups =
                new List<RoofPlaneGroup>();

            foreach (
                KeyValuePair<string, RoofPlaneGroup> pair
                in groups
            )
            {
                RoofPlaneGroup group =
                    pair.Value;

                if (
                    group == null ||
                    group.ProjectedArea <
                        minimumPlaneArea
                )
                {
                    continue;
                }

                acceptedGroups.Add(
                    group
                );
            }

            // Defensive fallback for small assets: keep the largest real
            // plane rather than fabricating a roof from tiny fragments.
            if (
                acceptedGroups.Count == 0 &&
                groups.Count > 0
            )
            {
                RoofPlaneGroup largest =
                    null;

                foreach (
                    KeyValuePair<string, RoofPlaneGroup> pair
                    in groups
                )
                {
                    RoofPlaneGroup group =
                        pair.Value;

                    if (
                        group != null &&
                        (
                            largest == null ||
                            group.ProjectedArea >
                                largest.ProjectedArea
                        )
                    )
                    {
                        largest =
                            group;
                    }
                }

                if (
                    largest != null &&
                    largest.ProjectedArea >=
                        Mathf.Max(
                            0.75f,
                            footprintArea *
                            0.025f
                        )
                )
                {
                    acceptedGroups.Add(
                        largest
                    );
                }
            }

            if (
                acceptedGroups.Count == 0
            )
            {
                ModLog.Checkpoint(
                    "STRUCTURE-ROOF skipped; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; reason=no-major-planes" +
                    "; candidates=" +
                    candidates.Count +
                    "; groups=" +
                    groups.Count
                );

                return null;
            }

            List<Vector3> vertices =
                new List<Vector3>();

            List<Vector2> uvs =
                new List<Vector2>();

            List<int> triangles =
                new List<int>();

            int acceptedTriangles =
                0;

            float acceptedProjectedArea =
                0f;

            float roofMaximumY =
                roofBaseY;

            for (
                int groupIndex = 0;
                groupIndex < acceptedGroups.Count;
                groupIndex++
            )
            {
                RoofPlaneGroup group =
                    acceptedGroups[
                        groupIndex
                    ];

                acceptedProjectedArea +=
                    group.ProjectedArea;

                for (
                    int triangleIndex = 0;
                    triangleIndex < group.Triangles.Count;
                    triangleIndex++
                )
                {
                    RoofTriangleCandidate candidate =
                        group.Triangles[
                            triangleIndex
                        ];

                    Vector3 localA =
                        new Vector3(
                            candidate.A.x,
                            candidate.A.y -
                            roofBaseY,
                            candidate.A.z
                        );

                    Vector3 localB =
                        new Vector3(
                            candidate.B.x,
                            candidate.B.y -
                            roofBaseY,
                            candidate.B.z
                        );

                    Vector3 localC =
                        new Vector3(
                            candidate.C.x,
                            candidate.C.y -
                            roofBaseY,
                            candidate.C.z
                        );

                    Vector3 localNormal =
                        Vector3.Cross(
                            localB -
                            localA,
                            localC -
                            localA
                        );

                    if (
                        localNormal.y <
                        0f
                    )
                    {
                        Vector3 swap =
                            localB;

                        localB =
                            localC;

                        localC =
                            swap;
                    }

                    int topStart =
                        vertices.Count;

                    vertices.Add(
                        localA
                    );

                    vertices.Add(
                        localB
                    );

                    vertices.Add(
                        localC
                    );

                    uvs.Add(
                        new Vector2(
                            localA.x,
                            localA.z
                        ) *
                        0.25f
                    );

                    uvs.Add(
                        new Vector2(
                            localB.x,
                            localB.z
                        ) *
                        0.25f
                    );

                    uvs.Add(
                        new Vector2(
                            localC.x,
                            localC.z
                        ) *
                        0.25f
                    );

                    triangles.Add(
                        topStart
                    );

                    triangles.Add(
                        topStart +
                        1
                    );

                    triangles.Add(
                        topStart +
                        2
                    );

                    int undersideStart =
                        vertices.Count;

                    vertices.Add(
                        localA
                    );

                    vertices.Add(
                        localC
                    );

                    vertices.Add(
                        localB
                    );

                    uvs.Add(
                        new Vector2(
                            localA.x,
                            localA.z
                        ) *
                        0.25f
                    );

                    uvs.Add(
                        new Vector2(
                            localC.x,
                            localC.z
                        ) *
                        0.25f
                    );

                    uvs.Add(
                        new Vector2(
                            localB.x,
                            localB.z
                        ) *
                        0.25f
                    );

                    triangles.Add(
                        undersideStart
                    );

                    triangles.Add(
                        undersideStart +
                        1
                    );

                    triangles.Add(
                        undersideStart +
                        2
                    );

                    roofMaximumY =
                        Mathf.Max(
                            roofMaximumY,
                            candidate.A.y
                        );

                    roofMaximumY =
                        Mathf.Max(
                            roofMaximumY,
                            candidate.B.y
                        );

                    roofMaximumY =
                        Mathf.Max(
                            roofMaximumY,
                            candidate.C.y
                        );

                    acceptedTriangles++;
                }
            }

            if (
                acceptedTriangles == 0
            )
            {
                return null;
            }

            Mesh mesh =
                new Mesh();

            mesh.name =
                "ConcreteRoofStructure_Mesh";

            if (
                vertices.Count >
                65000
            )
            {
                mesh.indexFormat =
                    UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(
                vertices
            );

            mesh.SetUVs(
                0,
                uvs
            );

            mesh.SetTriangles(
                triangles,
                0
            );

            mesh.RecalculateNormals();

            mesh.RecalculateBounds();

            GameObject roof =
                new GameObject(
                    "ConcreteRoofStructure"
                );

            roof.hideFlags =
                HideFlags.DontSave;

            roof.transform.SetParent(
                parent.transform,
                false
            );

            roof.transform.localPosition =
                new Vector3(
                    0f,
                    structuralTop,
                    0f
                );

            MeshFilter filter =
                roof.AddComponent<MeshFilter>();

            filter.sharedMesh =
                mesh;

            MeshRenderer renderer =
                roof.AddComponent<MeshRenderer>();

            renderer.sharedMaterial =
                m_BuildingConstructionMaterial;

            ConfigureStructureRenderer(
                renderer
            );

            visual.BuildingVisualMeshes.Add(
                mesh
            );

            ModLog.Checkpoint(
                "STRUCTURE-ROOF create; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; structuralTop=" +
                structuralTop.ToString(
                    "0.00"
                ) +
                "; mode=" +
                (
                    hasSlopedRoof
                        ? "sloped-major-planes"
                        : "flat-highest-plane"
                ) +
                "; roofHeight=" +
                Mathf.Max(
                    0f,
                    roofMaximumY -
                    roofBaseY
                ).ToString(
                    "0.00"
                ) +
                "; candidates=" +
                candidates.Count +
                "; groups=" +
                groups.Count +
                "; acceptedGroups=" +
                acceptedGroups.Count +
                "; acceptedTriangles=" +
                acceptedTriangles +
                "; acceptedProjectedArea=" +
                acceptedProjectedArea.ToString(
                    "0.00"
                ) +
                "; rejectedBelow=" +
                rejectedBelow +
                "; rejectedVertical=" +
                rejectedVertical +
                "; rejectedTiny=" +
                rejectedTiny
            );

            return roof;
        }

        private void UpdateFoldedBuildingVisual(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform,
            float visualProgress
        )
        {
            UpdateCutoffBuildingVisual(
                visual,
                sourceTransform,
                visualProgress
            );
        }

        private void DestroyFoldedBuildingVisual(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            if (
                visual.BuildingVisualRoot != null
            )
            {
                visual.BuildingVisualRoot.SetActive(
                    false
                );

                ScheduleUnityDestroy(
                    visual.BuildingVisualRoot
                );

                visual.BuildingVisualRoot =
                    null;

                visual.BuildingFoldRoot =
                    null;
            }

            for (
                int i = 0;
                i < visual.BuildingVisualMeshes.Count;
                i++
            )
            {
                Mesh mesh =
                    visual.BuildingVisualMeshes[i];

                if (
                    mesh != null
                )
                {
                    ScheduleUnityDestroy(
                        mesh
                    );
                }
            }

            visual.BuildingVisualMeshes.Clear();

            for (
                int i = 0;
                i < visual.BuildingVisualMaterials.Count;
                i++
            )
            {
                Material material =
                    visual.BuildingVisualMaterials[i];

                if (
                    material != null
                )
                {
                    ScheduleUnityDestroy(
                        material
                    );
                }
            }

            visual.BuildingVisualMaterials.Clear();

            visual.CutoffMeshes.Clear();

            visual.CutoffLocalMinY =
                0f;

            visual.CutoffLocalMaxY =
                0f;

            visual.LastCutoffHeight =
                0f;

            visual.HasCutoffHeight =
                false;

            visual.LastCutoffLoggedProgress =
                -1f;

            visual.ConcreteStructureRoot =
                null;

            visual.RoofStructureRoot =
                null;

            visual.ConcreteColumns.Clear();

            visual.ConcreteBeamLevels.Clear();

            visual.ConcreteSlabs.Clear();

            visual.ConcreteFloorFrames.Clear();

            for (
                int i = 0;
                i < visual.BuildingLoadedSurfaceAssets.Count;
                i++
            )
            {
                SurfaceAsset surfaceAsset =
                    visual.BuildingLoadedSurfaceAssets[i];

                if (
                    surfaceAsset == null
                )
                {
                    continue;
                }

                try
                {
                    surfaceAsset.Unload(
                        false
                    );
                }
                catch
                {
                }
            }

            visual.BuildingLoadedSurfaceAssets.Clear();
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
                visual.ScaffoldRoot == null ||
                visual.ScaffoldLevels.Count == 0
            )
            {
                return;
            }

            PositionScaffoldRoot(
                visual,
                sourceTransform
            );

            if (
                !UpdateScaffoldDistanceVisibility(
                    visual,
                    sourceTransform
                )
            )
            {
                return;
            }

            int levelCount =
                visual.ScaffoldLevels.Count;

            float progress =
                Mathf.Clamp01(
                    visualProgress
                );

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

            int fullyRevealedCount = 0;

            // Floor boundaries are sorted bottom-to-top, so a binary search
            // replaces the old O(levelCount) traversal every frame.
            int low = 0;
            int high = levelCount - 1;

            while (
                low <= high
            )
            {
                int mid =
                    (low + high) >> 1;

                float top =
                    visual.ScaffoldLevelBottoms[mid] +
                    visual.ScaffoldLevelHeights[mid];

                if (
                    top <=
                    scaffoldVisibleHeight +
                    0.001f
                )
                {
                    fullyRevealedCount =
                        mid + 1;

                    low =
                        mid + 1;
                }
                else
                {
                    high =
                        mid - 1;
                }
            }

            int partialIndex =
                fullyRevealedCount < levelCount
                    ? fullyRevealedCount
                    : -1;

            float partialReveal =
                0f;

            if (
                partialIndex >= 0
            )
            {
                partialReveal =
                    Mathf.Clamp01(
                        (
                            scaffoldVisibleHeight -
                            visual.ScaffoldLevelBottoms[partialIndex]
                        ) /
                        Mathf.Max(
                            visual.ScaffoldLevelHeights[partialIndex],
                            0.01f
                        )
                    );
            }

            if (
                fullyRevealedCount >
                visual.ScaffoldFullyRevealedCount
            )
            {
                for (
                    int i = visual.ScaffoldFullyRevealedCount;
                    i < fullyRevealedCount;
                    i++
                )
                {
                    SetScaffoldLevelState(
                        visual,
                        i,
                        true,
                        1f
                    );
                }
            }
            else if (
                fullyRevealedCount <
                visual.ScaffoldFullyRevealedCount
            )
            {
                for (
                    int i = fullyRevealedCount;
                    i < visual.ScaffoldFullyRevealedCount;
                    i++
                )
                {
                    SetScaffoldLevelState(
                        visual,
                        i,
                        false,
                        0f
                    );
                }
            }

            if (
                visual.ScaffoldPartialRevealIndex >= 0 &&
                visual.ScaffoldPartialRevealIndex != partialIndex &&
                visual.ScaffoldPartialRevealIndex >= fullyRevealedCount
            )
            {
                SetScaffoldLevelState(
                    visual,
                    visual.ScaffoldPartialRevealIndex,
                    false,
                    0f
                );
            }

            if (
                partialIndex >= 0
            )
            {
                if (
                    partialReveal <= 0.001f
                )
                {
                    SetScaffoldLevelState(
                        visual,
                        partialIndex,
                        false,
                        0f
                    );
                }
                else
                {
                    SetScaffoldLevelState(
                        visual,
                        partialIndex,
                        true,
                        Mathf.Max(
                            Smooth01(
                                partialReveal
                            ),
                            0.001f
                        )
                    );
                }
            }

            visual.ScaffoldFullyRevealedCount =
                fullyRevealedCount;

            visual.ScaffoldPartialRevealIndex =
                partialIndex;
        }

        private static void SetScaffoldLevelState(
            ConstructionVisual visual,
            int levelIndex,
            bool active,
            float verticalScale
        )
        {
            if (
                visual == null ||
                levelIndex < 0 ||
                levelIndex >= visual.ScaffoldLevels.Count
            )
            {
                return;
            }

            GameObject level =
                visual.ScaffoldLevels[levelIndex];

            if (
                level == null
            )
            {
                return;
            }

            if (
                level.activeSelf != active
            )
            {
                level.SetActive(
                    active
                );
            }

            if (
                active
            )
            {
                Vector3 scale =
                    level.transform.localScale;

                float y =
                    Mathf.Max(
                        verticalScale,
                        0.001f
                    );

                if (
                    Mathf.Abs(
                        scale.y - y
                    ) > 0.0005f
                )
                {
                    level.transform.localScale =
                        new Vector3(
                            1f,
                            y,
                            1f
                        );
                }
            }
        }

        private void PositionScaffoldRoot(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform
        )
        {
            if (
                visual == null ||
                visual.ScaffoldRoot == null
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
        }

        private void BeginScaffoldDismantling(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.Dismantling
            )
            {
                return;
            }

            visual.Dismantling =
                true;

            visual.ScaffoldFullyDismantledCount =
                0;

            visual.ScaffoldPartialDismantleIndex =
                -1;

            visual.DismantleStartTime =
                global::UnityEngine.Time.unscaledTime;

            visual.Suspended =
                false;

            visual.MissingFrames =
                0;

            // The vanilla completed asset has been visible through the hand-off
            // delay and the fade-out. Hide the remaining animated clone now;
            // scaffold dismantling starts from this exact moment.
            if (
                visual.BuildingVisualRoot != null
            )
            {
                visual.BuildingVisualRoot.SetActive(
                    false
                );
            }

            if (
                visual.ScaffoldRoot != null
            )
            {
                visual.ScaffoldRoot.SetActive(
                    true
                );

                visual.ScaffoldDistanceVisible =
                    true;

                visual.NextScaffoldDistanceCheckTime =
                    0f;
            }

            ModLog.Checkpoint(
                "SCAFFOLD dismantling begin; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; duration=" +
                ScaffoldDismantleDuration.ToString(
                    "0.0"
                ) +
                "s; animatedAssetHidden=True; completionHold=" +
                CompletedAssetHoldDuration.ToString(
                    "0.0"
                ) +
                "s; fade=" +
                CompletedAssetFadeDuration.ToString(
                    "0.00"
                ) +
                "s"
            );
        }

        private bool UpdateCompletedConstructionVisual(
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
                return true;
            }

            if (
                !EntityManager.HasComponent<Game.Objects.Transform>(
                    visual.Source
                )
            )
            {
                return true;
            }

            Game.Objects.Transform sourceTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    visual.Source
                );

            PositionScaffoldRoot(
                visual,
                sourceTransform
            );

            UpdateCranePosition(
                visual,
                sourceTransform
            );

            if (
                !visual.CompletionHoldStarted
            )
            {
                visual.CompletionHoldStarted =
                    true;

                visual.CompletionHoldStartTime =
                    global::UnityEngine.Time.unscaledTime;

                visual.Suspended =
                    false;

                if (
                    visual.BuildingVisualRoot != null
                )
                {
                    visual.BuildingVisualRoot.SetActive(
                        true
                    );
                }

                if (
                    visual.ScaffoldRoot != null
                )
                {
                    visual.ScaffoldRoot.SetActive(
                        true
                    );
                }

                ModLog.Checkpoint(
                    "COMPLETION hold begin; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; duration=" +
                    CompletedAssetHoldDuration.ToString(
                        "0.0"
                    ) +
                    "s; animatedAssetVisible=True"
                );
            }

            if (
                !visual.Dismantling
            )
            {
                float now =
                    global::UnityEngine.Time.unscaledTime;

                float completionHoldElapsed =
                    Mathf.Max(
                        0f,
                        now -
                        visual.CompletionHoldStartTime
                    );

                if (
                    completionHoldElapsed <
                    CompletedAssetHoldDuration
                )
                {
                    return false;
                }

                if (
                    !visual.CompletedAssetFadeStarted
                )
                {
                    visual.CompletedAssetFadeStarted =
                        true;

                    visual.CompletedAssetFadeStartTime =
                        now;

                    SetCompletedAssetFade(
                        visual,
                        1f
                    );

                    ModLog.Checkpoint(
                        "COMPLETION fade begin; source=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; duration=" +
                        CompletedAssetFadeDuration.ToString(
                            "0.00"
                        ) +
                        "s"
                    );
                }

                float fadeElapsed =
                    Mathf.Max(
                        0f,
                        now -
                        visual.CompletedAssetFadeStartTime
                    );

                float fadeProgress =
                    Mathf.Clamp01(
                        fadeElapsed /
                        Mathf.Max(
                            CompletedAssetFadeDuration,
                            0.01f
                        )
                    );

                SetCompletedAssetFade(
                    visual,
                    1f -
                    Smooth01(
                        fadeProgress
                    )
                );

                if (
                    fadeProgress <
                    1f
                )
                {
                    return false;
                }

                ModLog.Checkpoint(
                    "COMPLETION fade complete; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version
                );

                BeginScaffoldDismantling(
                    visual
                );
            }

            if (
                visual.ScaffoldRoot == null ||
                visual.ScaffoldLevels.Count == 0
            )
            {
                return true;
            }

            if (
                !UpdateScaffoldDistanceVisibility(
                    visual,
                    sourceTransform
                )
            )
            {
                // Dismantling continues by time even while culled.
            }

            float elapsed =
                Mathf.Max(
                    0f,
                    global::UnityEngine.Time.unscaledTime -
                    visual.DismantleStartTime
                );

            float dismantleProgress =
                Mathf.Clamp01(
                    elapsed /
                    Mathf.Max(
                        ScaffoldDismantleDuration,
                        0.01f
                    )
                );

            dismantleProgress =
                Smooth01(
                    dismantleProgress
                );

            int levelCount =
                visual.ScaffoldLevels.Count;

            float dismantledLevelUnits =
                dismantleProgress *
                levelCount;

            int fullyDismantledCount =
                Mathf.Clamp(
                    Mathf.FloorToInt(
                        dismantledLevelUnits
                    ),
                    0,
                    levelCount
                );

            int partialDismantleIndex =
                fullyDismantledCount < levelCount
                    ? levelCount -
                      1 -
                      fullyDismantledCount
                    : -1;

            float partialDismantle =
                fullyDismantledCount < levelCount
                    ? dismantledLevelUnits -
                      fullyDismantledCount
                    : 0f;

            if (
                fullyDismantledCount >
                visual.ScaffoldFullyDismantledCount
            )
            {
                for (
                    int count = visual.ScaffoldFullyDismantledCount;
                    count < fullyDismantledCount;
                    count++
                )
                {
                    int index =
                        levelCount -
                        1 -
                        count;

                    SetScaffoldLevelState(
                        visual,
                        index,
                        false,
                        0f
                    );
                }
            }

            if (
                visual.ScaffoldPartialDismantleIndex >= 0 &&
                visual.ScaffoldPartialDismantleIndex !=
                    partialDismantleIndex &&
                visual.ScaffoldPartialDismantleIndex <
                    levelCount -
                    fullyDismantledCount
            )
            {
                SetScaffoldLevelState(
                    visual,
                    visual.ScaffoldPartialDismantleIndex,
                    true,
                    1f
                );
            }

            float remainingScale =
                1f;

            if (
                partialDismantleIndex >= 0
            )
            {
                remainingScale =
                    1f -
                    Smooth01(
                        Mathf.Clamp01(
                            partialDismantle
                        )
                    );

                SetScaffoldLevelState(
                    visual,
                    partialDismantleIndex,
                    true,
                    Mathf.Max(
                        remainingScale,
                        0.001f
                    )
                );
            }

            visual.ScaffoldFullyDismantledCount =
                fullyDismantledCount;

            visual.ScaffoldPartialDismantleIndex =
                partialDismantleIndex;

            float highestRemainingHeight =
                0f;

            if (
                partialDismantleIndex >= 0
            )
            {
                highestRemainingHeight =
                    visual.ScaffoldLevelBottoms[
                        partialDismantleIndex
                    ] +
                    visual.ScaffoldLevelHeights[
                        partialDismantleIndex
                    ] *
                    remainingScale;
            }
            else if (
                levelCount -
                fullyDismantledCount > 0
            )
            {
                int highestIndex =
                    levelCount -
                    fullyDismantledCount -
                    1;

                highestRemainingHeight =
                    visual.ScaffoldLevelBottoms[
                        highestIndex
                    ] +
                    visual.ScaffoldLevelHeights[
                        highestIndex
                    ];
            }

            UpdateCompanyBannerVisibility(
                visual,
                highestRemainingHeight,
                highestRemainingHeight >
                0.001f
            );

            if (
                dismantleProgress >=
                0.999f
            )
            {
                ModLog.Checkpoint(
                    "SCAFFOLD dismantling complete; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; animatedAssetHidden=True; completionHoldElapsed=True"
                );

                return true;
            }

            return false;
        }

        private void ReadBuildingLotDimensions(
            Entity prefab,
            ConstructionVisual visual
        )
        {
            visual.HasLotDimensions =
                false;

            visual.LotHalfWidth =
                0f;

            visual.LotHalfDepth =
                0f;

            try
            {
                if (
                    prefab == Entity.Null ||
                    !EntityManager.Exists(
                        prefab
                    ) ||
                    !EntityManager.HasComponent<Game.Prefabs.BuildingData>(
                        prefab
                    )
                )
                {
                    return;
                }

                Game.Prefabs.BuildingData buildingData =
                    EntityManager.GetComponentData<Game.Prefabs.BuildingData>(
                        prefab
                    );

                float lotWidth =
                    buildingData.m_LotSize.x *
                    8f;

                float lotDepth =
                    buildingData.m_LotSize.y *
                    8f;

                if (
                    lotWidth < 2f ||
                    lotDepth < 2f
                )
                {
                    return;
                }

                visual.LotHalfWidth =
                    lotWidth *
                    0.5f;

                visual.LotHalfDepth =
                    lotDepth *
                    0.5f;

                visual.HasLotDimensions =
                    true;
            }
            catch
            {
                visual.HasLotDimensions =
                    false;
            }
        }

        private void UpdateScaffoldShadowLod(
            ConstructionVisual visual,
            float distance
        )
        {
            if (
                visual == null ||
                visual.ScaffoldRenderers == null
            )
            {
                return;
            }

            bool shadowsEnabled =
                distance <=
                ScaffoldShadowCullDistance;

            if (
                shadowsEnabled ==
                visual.ScaffoldShadowsEnabled
            )
            {
                return;
            }

            UnityEngine.Rendering.ShadowCastingMode mode =
                shadowsEnabled
                    ? UnityEngine.Rendering.ShadowCastingMode.On
                    : UnityEngine.Rendering.ShadowCastingMode.Off;

            for (
                int i = 0;
                i < visual.ScaffoldRenderers.Count;
                i++
            )
            {
                MeshRenderer renderer =
                    visual.ScaffoldRenderers[i];

                if (
                    renderer == null
                )
                {
                    continue;
                }

                renderer.shadowCastingMode =
                    mode;
            }

            visual.ScaffoldShadowsEnabled =
                shadowsEnabled;

            ModLog.Checkpoint(
                "SCAFFOLD shadow-lod; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; enabled=" +
                shadowsEnabled +
                "; distance=" +
                distance.ToString("0.0")
            );
        }

        private bool UpdateScaffoldDistanceVisibility(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform
        )
        {
            if (
                visual == null ||
                visual.ScaffoldRoot == null
            )
            {
                return false;
            }

            float now =
                UnityEngine.Time.unscaledTime;

            if (
                now <
                visual.NextScaffoldDistanceCheckTime
            )
            {
                return
                    visual.ScaffoldDistanceVisible;
            }

            visual.NextScaffoldDistanceCheckTime =
                now +
                ScaffoldDistanceCheckInterval +
                Mathf.Abs(
                    visual.Source.Index %
                    11
                ) *
                0.013f;

            Camera renderCamera =
                GetRenderCamera(
                    now
                );

            if (
                renderCamera == null
            )
            {
                if (
                    !visual.ScaffoldShadowsEnabled
                )
                {
                    UpdateScaffoldShadowLod(
                        visual,
                        0f
                    );
                }

                visual.ScaffoldDistanceVisible =
                    true;

                if (
                    !visual.ScaffoldRoot.activeSelf
                )
                {
                    visual.ScaffoldRoot.SetActive(
                        true
                    );
                }

                return true;
            }

            Vector3 cameraPosition =
                renderCamera.transform.position;

            Vector3 scaffoldPosition =
                new Vector3(
                    sourceTransform.m_Position.x,
                    sourceTransform.m_Position.y +
                    visual.BuildingHeight *
                    0.5f,
                    sourceTransform.m_Position.z
                );

            float distance =
                Vector3.Distance(
                    cameraPosition,
                    scaffoldPosition
                );

            UpdateScaffoldShadowLod(
                visual,
                distance
            );

            float footprintSize =
                Mathf.Max(
                    visual.BuildingSize.x,
                    visual.BuildingSize.z
                );

            float cullDistance =
                Mathf.Clamp(
                    ScaffoldMinimumCullDistance +
                    visual.BuildingHeight *
                    ScaffoldCullDistancePerHeightMetre +
                    footprintSize,
                    ScaffoldMinimumCullDistance,
                    ScaffoldMaximumCullDistance
                );

            bool shouldBeVisible =
                visual.ScaffoldDistanceVisible
                    ? distance <=
                      cullDistance +
                      ScaffoldCullHysteresis
                    : distance <=
                      cullDistance -
                      ScaffoldCullHysteresis;

            if (
                shouldBeVisible !=
                visual.ScaffoldDistanceVisible
            )
            {
                visual.ScaffoldDistanceVisible =
                    shouldBeVisible;

                visual.ScaffoldRoot.SetActive(
                    shouldBeVisible
                );
            }

            return
                visual.ScaffoldDistanceVisible;
        }

        private Camera GetRenderCamera(
            float now
        )
        {
            if (
                m_RenderCamera != null &&
                m_RenderCamera.isActiveAndEnabled
            )
            {
                return m_RenderCamera;
            }

            if (
                now <
                m_NextRenderCameraSearchTime
            )
            {
                return null;
            }

            m_NextRenderCameraSearchTime =
                now +
                1f;

            try
            {
                m_RenderCamera =
                    Camera.main;
            }
            catch
            {
                m_RenderCamera =
                    null;
            }

            if (
                m_RenderCamera != null &&
                m_RenderCamera.isActiveAndEnabled
            )
            {
                return m_RenderCamera;
            }

            Camera[] cameras;

            try
            {
                cameras =
                    Camera.allCameras;
            }
            catch
            {
                cameras =
                    Array.Empty<Camera>();
            }

            Camera bestCamera =
                null;

            for (
                int i = 0;
                i < cameras.Length;
                i++
            )
            {
                Camera camera =
                    cameras[i];

                if (
                    camera == null ||
                    !camera.isActiveAndEnabled ||
                    camera.cameraType !=
                    CameraType.Game
                )
                {
                    continue;
                }

                if (
                    bestCamera == null ||
                    camera.depth >
                    bestCamera.depth
                )
                {
                    bestCamera =
                        camera;
                }
            }

            m_RenderCamera =
                bestCamera;

            return m_RenderCamera;
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
            ModLog.Checkpoint(
                "SCAFFOLD create begin; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; previousMeshes=" +
                visual.ScaffoldMeshes.Count
            );

            DestroyScaffold(
                visual
            );

            visual.ScaffoldRoot =
                new GameObject(
                    $"ConstructionAnimation_V1_42_36_Scaffold_" +
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

            visual.ScaffoldRenderers =
                new List<MeshRenderer>();

            visual.ScaffoldFullyRevealedCount =
                0;

            visual.ScaffoldPartialRevealIndex =
                -1;

            visual.ScaffoldFullyDismantledCount =
                0;

            visual.ScaffoldPartialDismantleIndex =
                -1;

            visual.ScaffoldShadowsEnabled =
                true;

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

                CreateScaffoldLevelDirect(
                    visual,
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

            ModLog.Checkpoint(
                "SCAFFOLD create complete; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; floors=" +
                visual.ScaffoldLevels.Count +
                "; meshes=" +
                visual.ScaffoldMeshes.Count +
                "; height=" +
                visual.ScaffoldHeight.ToString("0.00")
            );

            ModLog.Info(
                $"V1.42.5 scaffold created: " +
                $"source={visual.Source.Index}:" +
                $"{visual.Source.Version}, " +
                $"floors={visual.ScaffoldLevels.Count}, " +
                $"height={visual.ScaffoldHeight:0.00}m"
            );
        }

        private void CreateScaffoldLevelDirect(
            ConstructionVisual visual,
            List<Vector2> outline,
            GameObject levelRoot,
            float levelHeight,
            bool createBottomRing
        )
        {
            if (
                visual == null ||
                outline == null ||
                outline.Count < 3 ||
                levelRoot == null
            )
            {
                return;
            }

            ScaffoldGeometryBuffer buffer =
                new ScaffoldGeometryBuffer();

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
                    outline[edgeIndex];

                Vector2 b =
                    outline[
                        (edgeIndex + 1) %
                        outline.Count
                    ];

                float edgeLength =
                    Vector2.Distance(
                        a,
                        b
                    );

                if (
                    edgeLength < 0.25f
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
                    Vector2 p =
                        Vector2.Lerp(
                            a,
                            b,
                            bay /
                            (float)bays
                        );

                    AppendScaffoldBeam(
                        buffer,
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
                        false
                    );
                }

                if (
                    createBottomRing
                )
                {
                    AppendScaffoldBeam(
                        buffer,
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
                        false
                    );
                }

                AppendScaffoldBeam(
                    buffer,
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
                    false
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
                            (bay + 1) /
                            (float)bays
                        );

                    AppendScaffoldBeam(
                        buffer,
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
                        false
                    );

                    AppendScaffoldBeam(
                        buffer,
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
                        false
                    );
                }

                AppendScaffoldDeckAlongEdge(
                    buffer,
                    a,
                    b,
                    topY +
                    0.05f
                );
            }

            AppendHorizontalFloorGrid(
                buffer,
                outline,
                topY
            );

            if (
                buffer.Vertices.Count == 0
            )
            {
                return;
            }

            Mesh mesh =
                new Mesh();

            mesh.name =
                "ScaffoldLevelDirect_" +
                visual.Source.Index;

            mesh.hideFlags =
                HideFlags.DontSave;

            if (
                buffer.Vertices.Count >
                60000
            )
            {
                mesh.indexFormat =
                    UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(
                buffer.Vertices
            );

            mesh.SetNormals(
                buffer.Normals
            );

            mesh.SetUVs(
                0,
                buffer.UVs
            );

            mesh.subMeshCount =
                2;

            mesh.SetTriangles(
                buffer.MetalTriangles,
                0,
                false
            );

            mesh.SetTriangles(
                buffer.DeckTriangles,
                1,
                false
            );

            mesh.RecalculateBounds();

            MeshFilter filter =
                levelRoot.AddComponent<MeshFilter>();

            filter.sharedMesh =
                mesh;

            MeshRenderer renderer =
                levelRoot.AddComponent<MeshRenderer>();

            renderer.sharedMaterials =
                new Material[]
                {
                    m_ScaffoldMetalMaterial,
                    m_ScaffoldDeckMaterial
                };

            ConfigureScaffoldRenderer(
                renderer
            );

            visual.ScaffoldMeshes.Add(
                mesh
            );

            visual.ScaffoldRenderers.Add(
                renderer
            );

            ModLog.Checkpoint(
                "SCAFFOLD direct-mesh; source=" +
                visual.Source.Index +
                ":" +
                visual.Source.Version +
                "; vertices=" +
                mesh.vertexCount +
                "; subMeshes=" +
                mesh.subMeshCount +
                "; tempChildren=0"
            );
        }

        private static void AppendScaffoldBeam(
            ScaffoldGeometryBuffer buffer,
            Vector3 start,
            Vector3 end,
            float thickness,
            bool deck
        )
        {
            Vector3 direction =
                end - start;

            float length =
                direction.magnitude;

            if (
                buffer == null ||
                length < 0.001f
            )
            {
                return;
            }

            AppendScaffoldBox(
                buffer,
                (start + end) *
                0.5f,
                new Vector3(
                    thickness,
                    thickness,
                    length
                ),
                direction /
                length,
                deck
            );
        }

        private static void AppendScaffoldDeckAlongEdge(
            ScaffoldGeometryBuffer buffer,
            Vector2 a,
            Vector2 b,
            float y
        )
        {
            Vector2 direction2D =
                b - a;

            float length =
                direction2D.magnitude;

            if (
                buffer == null ||
                length < 0.25f
            )
            {
                return;
            }

            direction2D /=
                length;

            Vector2 lateral2D =
                new Vector2(
                    -direction2D.y,
                    direction2D.x
                );

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

            Vector2 center =
                (a + b) *
                0.5f;

            float visualLength =
                Mathf.Max(
                    0.10f,
                    length -
                    0.035f
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
                    (plankIndex + 0.5f);

                Vector2 plankCenter =
                    center +
                    lateral2D *
                    lateralOffset;

                AppendScaffoldBox(
                    buffer,
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
                    true
                );
            }
        }

        private static void AppendHorizontalFloorGrid(
            ScaffoldGeometryBuffer buffer,
            List<Vector2> outline,
            float y
        )
        {
            if (
                buffer == null ||
                outline == null ||
                outline.Count < 3
            )
            {
                return;
            }

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;

            for (
                int i = 0;
                i < outline.Count;
                i++
            )
            {
                minX = Mathf.Min(minX, outline[i].x);
                maxX = Mathf.Max(maxX, outline[i].x);
                minZ = Mathf.Min(minZ, outline[i].y);
                maxZ = Mathf.Max(maxZ, outline[i].y);
            }

            float startX =
                Mathf.Ceil(
                    minX /
                    ScaffoldGridSpacing
                ) *
                ScaffoldGridSpacing;

            for (
                float x = startX;
                x <= maxX + 0.001f;
                x += ScaffoldGridSpacing
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
                    float z0 = intersections[i];
                    float z1 = intersections[i + 1];

                    if (
                        z1 - z0 < 0.15f
                    )
                    {
                        continue;
                    }

                    AppendScaffoldBeam(
                        buffer,
                        new Vector3(x, y, z0),
                        new Vector3(x, y, z1),
                        ScaffoldGridBeamThickness,
                        false
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
                float z = startZ;
                z <= maxZ + 0.001f;
                z += ScaffoldGridSpacing
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
                    float x0 = intersections[i];
                    float x1 = intersections[i + 1];

                    if (
                        x1 - x0 < 0.15f
                    )
                    {
                        continue;
                    }

                    AppendScaffoldBeam(
                        buffer,
                        new Vector3(x0, y, z),
                        new Vector3(x1, y, z),
                        ScaffoldGridBeamThickness,
                        false
                    );
                }
            }
        }

        private static void AppendScaffoldBox(
            ScaffoldGeometryBuffer buffer,
            Vector3 center,
            Vector3 size,
            Vector3 forward,
            bool deck
        )
        {
            if (
                buffer == null
            )
            {
                return;
            }

            Quaternion rotation =
                forward.sqrMagnitude > 0.000001f
                    ? Quaternion.LookRotation(
                        forward.normalized,
                        Vector3.up
                    )
                    : Quaternion.identity;

            Vector3 half =
                size *
                0.5f;

            List<int> triangles =
                deck
                    ? buffer.DeckTriangles
                    : buffer.MetalTriangles;

            for (
                int face = 0;
                face < 6;
                face++
            )
            {
                int baseIndex =
                    buffer.Vertices.Count;

                for (
                    int corner = 0;
                    corner < 4;
                    corner++
                )
                {
                    int cornerIndex =
                        ScaffoldCubeFaceCorners[
                            face * 4 +
                            corner
                        ];

                    Vector3 unitCorner =
                        ScaffoldUnitCubeCorners[
                            cornerIndex
                        ];

                    Vector3 local =
                        new Vector3(
                            unitCorner.x * half.x,
                            unitCorner.y * half.y,
                            unitCorner.z * half.z
                        );

                    buffer.Vertices.Add(
                        center +
                        rotation *
                        local
                    );

                    buffer.Normals.Add(
                        rotation *
                        ScaffoldCubeFaceNormals[face]
                    );
                }

                buffer.UVs.Add(new Vector2(0f, 0f));
                buffer.UVs.Add(new Vector2(1f, 0f));
                buffer.UVs.Add(new Vector2(1f, 1f));
                buffer.UVs.Add(new Vector2(0f, 1f));

                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }
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
                    0.25f
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

        private void CombineScaffoldLevelGeometry(
            ConstructionVisual visual,
            GameObject levelRoot
        )
        {
            if (
                visual == null ||
                levelRoot == null
            )
            {
                return;
            }

            MeshFilter[] filters =
                levelRoot.GetComponentsInChildren<MeshFilter>(
                    true
                );

            if (
                filters == null ||
                filters.Length == 0
            )
            {
                return;
            }

            List<MeshFilter> metalFilters =
                new List<MeshFilter>();

            List<MeshFilter> deckFilters =
                new List<MeshFilter>();

            for (
                int i = 0;
                i < filters.Length;
                i++
            )
            {
                MeshFilter filter =
                    filters[i];

                if (
                    filter == null ||
                    filter.sharedMesh == null ||
                    filter.gameObject == levelRoot
                )
                {
                    continue;
                }

                MeshRenderer renderer =
                    filter.GetComponent<MeshRenderer>();

                if (
                    renderer == null
                )
                {
                    continue;
                }

                if (
                    renderer.sharedMaterial ==
                    m_ScaffoldDeckMaterial
                )
                {
                    deckFilters.Add(
                        filter
                    );
                }
                else
                {
                    metalFilters.Add(
                        filter
                    );
                }
            }

            Mesh metalMesh =
                BuildCombinedScaffoldGroupMesh(
                    levelRoot,
                    metalFilters,
                    "ScaffoldMetalBatch_" +
                    visual.Source.Index
                );

            Mesh deckMesh =
                BuildCombinedScaffoldGroupMesh(
                    levelRoot,
                    deckFilters,
                    "ScaffoldWoodBatch_" +
                    visual.Source.Index
                );

            try
            {
                List<CombineInstance> groups =
                    new List<CombineInstance>();

                List<Material> materials =
                    new List<Material>();

                int totalVertexCount =
                    0;

                if (
                    metalMesh != null
                )
                {
                    groups.Add(
                        new CombineInstance
                        {
                            mesh = metalMesh,
                            transform = Matrix4x4.identity
                        }
                    );

                    materials.Add(
                        m_ScaffoldMetalMaterial
                    );

                    totalVertexCount +=
                        metalMesh.vertexCount;
                }

                if (
                    deckMesh != null
                )
                {
                    groups.Add(
                        new CombineInstance
                        {
                            mesh = deckMesh,
                            transform = Matrix4x4.identity
                        }
                    );

                    materials.Add(
                        m_ScaffoldDeckMaterial
                    );

                    totalVertexCount +=
                        deckMesh.vertexCount;
                }

                if (
                    groups.Count == 0
                )
                {
                    return;
                }

                Mesh combinedMesh =
                    new Mesh();

                combinedMesh.name =
                    "ScaffoldLevelCombined_" +
                    visual.Source.Index;

                combinedMesh.hideFlags =
                    HideFlags.DontSave;

                if (
                    totalVertexCount >
                    60000
                )
                {
                    combinedMesh.indexFormat =
                        UnityEngine.Rendering.IndexFormat.UInt32;
                }

                combinedMesh.CombineMeshes(
                    groups.ToArray(),
                    false,
                    false,
                    false
                );

                combinedMesh.RecalculateBounds();

                GameObject combinedObject =
                    new GameObject(
                        "ScaffoldLevelCombined"
                    );

                combinedObject.hideFlags =
                    HideFlags.DontSave;

                combinedObject.transform.SetParent(
                    levelRoot.transform,
                    false
                );

                combinedObject.transform.localPosition =
                    Vector3.zero;

                combinedObject.transform.localRotation =
                    Quaternion.identity;

                combinedObject.transform.localScale =
                    Vector3.one;

                MeshFilter combinedFilter =
                    combinedObject.AddComponent<MeshFilter>();

                combinedFilter.sharedMesh =
                    combinedMesh;

                MeshRenderer combinedRenderer =
                    combinedObject.AddComponent<MeshRenderer>();

                combinedRenderer.sharedMaterials =
                    materials.ToArray();

                ConfigureScaffoldRenderer(
                    combinedRenderer
                );

                visual.ScaffoldMeshes.Add(
                    combinedMesh
                );

                ModLog.Checkpoint(
                    "SCAFFOLD level-batch; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; renderers=1; subMeshCount=" +
                    combinedMesh.subMeshCount +
                    "; vertices=" +
                    combinedMesh.vertexCount
                );

                for (
                    int i = 0;
                    i < filters.Length;
                    i++
                )
                {
                    MeshFilter filter =
                        filters[i];

                    if (
                        filter == null ||
                        filter.gameObject == levelRoot ||
                        filter.gameObject == combinedObject
                    )
                    {
                        continue;
                    }

                    UnityEngine.Object.Destroy(
                        filter.gameObject
                    );
                }
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.47.4.3.5 scaffold level batch failed; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );

                for (
                    int i = 0;
                    i < filters.Length;
                    i++
                )
                {
                    MeshRenderer renderer =
                        filters[i] != null
                            ? filters[i].GetComponent<MeshRenderer>()
                            : null;

                    ConfigureScaffoldRenderer(
                        renderer
                    );
                }
            }
            finally
            {
                if (
                    metalMesh != null
                )
                {
                    UnityEngine.Object.Destroy(
                        metalMesh
                    );
                }

                if (
                    deckMesh != null
                )
                {
                    UnityEngine.Object.Destroy(
                        deckMesh
                    );
                }
            }
        }

        private Mesh BuildCombinedScaffoldGroupMesh(
            GameObject levelRoot,
            List<MeshFilter> filters,
            string meshName
        )
        {
            if (
                levelRoot == null ||
                filters == null ||
                filters.Count == 0
            )
            {
                return null;
            }

            CombineInstance[] combines =
                new CombineInstance[
                    filters.Count
                ];

            int totalVertexCount =
                0;

            Matrix4x4 worldToLevel =
                levelRoot.transform.worldToLocalMatrix;

            for (
                int i = 0;
                i < filters.Count;
                i++
            )
            {
                MeshFilter filter =
                    filters[i];

                combines[i].mesh =
                    filter.sharedMesh;

                combines[i].transform =
                    worldToLevel *
                    filter.transform.localToWorldMatrix;

                totalVertexCount +=
                    filter.sharedMesh.vertexCount;
            }

            Mesh combinedMesh =
                new Mesh();

            combinedMesh.name =
                meshName;

            combinedMesh.hideFlags =
                HideFlags.DontSave;

            if (
                totalVertexCount >
                60000
            )
            {
                combinedMesh.indexFormat =
                    UnityEngine.Rendering.IndexFormat.UInt32;
            }

            combinedMesh.CombineMeshes(
                combines,
                true,
                true,
                false
            );

            combinedMesh.RecalculateBounds();

            return combinedMesh;
        }

        private static void ConfigureStructureRenderer(
            MeshRenderer renderer
        )
        {
            if (
                renderer == null
            )
            {
                return;
            }

            renderer.allowOcclusionWhenDynamic =
                true;

            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.On;

            renderer.receiveShadows =
                true;
        }

        private static void ConfigureScaffoldRenderer(
            MeshRenderer renderer
        )
        {
            if (
                renderer == null
            )
            {
                return;
            }

            renderer.allowOcclusionWhenDynamic =
                true;

            renderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.On;

            renderer.receiveShadows =
                true;
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
                    length < 0.25f
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
                !visual.CraneEligibilityEvaluated
            )
            {
                visual.CraneEligible =
                    IsCraneEligible(
                        visual,
                        out visual.CraneSourcePrefabName
                    );

                visual.CraneEligibilityEvaluated =
                    true;

                ModLog.Checkpoint(
                    "CRANE eligibility; building=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; prefab=" +
                    (
                        visual.CraneSourcePrefabName ??
                        "<unknown>"
                    ) +
                    "; height=" +
                    visual.BuildingHeight.ToString("0.00") +
                    "; eligible=" +
                    visual.CraneEligible
                );
            }

            if (
                !visual.CraneEligible
            )
            {
                ParkIneligibleVanillaCrane(
                    visual,
                    sourceTransform
                );

                return;
            }

            if (
                !IsUsableCraneEntity(
                    visual.CraneEntity
                )
            )
            {
                Entity foundCrane =
                    FindCraneSubObject(
                        visual.Source
                    );

                if (
                    IsUsableCraneEntity(
                        foundCrane
                    )
                )
                {
                    visual.CraneEntity =
                        foundCrane;

                    visual.CraneUsingBackup =
                        false;

                    visual.CraneVerticalOffsetCaptured =
                        false;

                    visual.CranePositionLogged =
                        false;
                }
                else if (
                    IsUsableCraneEntity(
                        visual.CraneBackupEntity
                    )
                )
                {
                    visual.CraneEntity =
                        visual.CraneBackupEntity;

                    visual.CraneUsingBackup =
                        true;

                    visual.CranePositionLogged =
                        false;

                    ModLog.Checkpoint(
                        "CRANE backup activated; building=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version +
                        "; crane=" +
                        visual.CraneBackupEntity.Index +
                        ":" +
                        visual.CraneBackupEntity.Version
                    );
                }
            }

            if (
                !IsUsableCraneEntity(
                    visual.CraneEntity
                )
            )
            {
                return;
            }

            int cornerIndex;

            Vector2 localCranePosition;

            if (
                !TryGetCraneLotCorner(
                    visual,
                    out localCranePosition,
                    out cornerIndex
                )
            )
            {
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

                cornerIndex =
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

                localCranePosition =
                    corner +
                    outward *
                    (
                        ScaffoldMargin +
                        0.75f
                    );
            }

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

            if (
                !visual.CraneUsingBackup &&
                visual.CraneBackupEntity == Entity.Null
            )
            {
                EnsureCraneBackup(
                    visual,
                    visual.CraneEntity,
                    sourceTransform
                );
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
                    $"V1.43.47.4.3.14 crane positioned " +
                    $"building={visual.Source.Index}:{visual.Source.Version}; " +
                    $"crane={visual.CraneEntity.Index}:{visual.CraneEntity.Version}; " +
                    $"backup={visual.CraneUsingBackup}; " +
                    $"corner={cornerIndex}; " +
                    $"local=({localCranePosition.x:0.00}," +
                    $"{localCranePosition.y:0.00}); " +
                    $"verticalOffset={visual.CraneVerticalOffset:0.00}"
                );

                visual.CranePositionLogged =
                    true;
            }
        }

        private bool IsCraneEligible(
            ConstructionVisual visual,
            out string prefabName
        )
        {
            prefabName =
                null;

            if (
                visual == null ||
                visual.Source == Entity.Null ||
                !EntityManager.Exists(
                    visual.Source
                ) ||
                !EntityManager.HasComponent<PrefabRef>(
                    visual.Source
                )
            )
            {
                return true;
            }

            try
            {
                PrefabRef prefabRef =
                    EntityManager.GetComponentData<PrefabRef>(
                        visual.Source
                    );

                if (
                    prefabRef.m_Prefab != Entity.Null
                )
                {
                    prefabName =
                        m_PrefabSystem.GetPrefabName(
                            prefabRef.m_Prefab
                        );
                }
            }
            catch
            {
                return true;
            }

            if (
                string.IsNullOrWhiteSpace(
                    prefabName
                )
            )
            {
                return true;
            }

            string lowerName =
                prefabName.ToLowerInvariant();

            bool isRowHouse =
                lowerName.Contains("rowhouse") ||
                lowerName.Contains("row_house") ||
                lowerName.Contains("row house") ||
                lowerName.Contains("row");

            bool isResidential =
                lowerName.Contains("residential") ||
                lowerName.Contains("house");

            if (
                isRowHouse
            )
            {
                return false;
            }

            if (
                isResidential &&
                visual.BuildingHeight <=
                    CraneLowResidentialMaximumHeight
            )
            {
                return false;
            }

            return true;
        }

        private bool IsUsableCraneEntity(
            Entity crane
        )
        {
            return
                crane != Entity.Null &&
                EntityManager.Exists(
                    crane
                ) &&
                EntityManager.HasComponent<Game.Objects.Transform>(
                    crane
                ) &&
                !EntityManager.HasComponent<Deleted>(
                    crane
                ) &&
                !EntityManager.HasComponent<Game.Tools.Hidden>(
                    crane
                );
        }

        private void EnsureCraneBackup(
            ConstructionVisual visual,
            Entity sourceCrane,
            Game.Objects.Transform sourceTransform
        )
        {
            if (
                visual == null ||
                visual.CraneBackupEntity != Entity.Null ||
                !IsUsableCraneEntity(
                    sourceCrane
                )
            )
            {
                return;
            }

            try
            {
                Entity backup =
                    EntityManager.Instantiate(
                        sourceCrane
                    );

                if (
                    EntityManager.HasComponent<Owner>(
                        backup
                    )
                )
                {
                    EntityManager.RemoveComponent<Owner>(
                        backup
                    );
                }

                if (
                    EntityManager.HasComponent<Deleted>(
                        backup
                    )
                )
                {
                    EntityManager.RemoveComponent<Deleted>(
                        backup
                    );
                }

                if (
                    EntityManager.HasComponent<Game.Tools.Hidden>(
                        backup
                    )
                )
                {
                    EntityManager.RemoveComponent<Game.Tools.Hidden>(
                        backup
                    );
                }

                Game.Objects.Transform backupTransform =
                    EntityManager.GetComponentData<Game.Objects.Transform>(
                        backup
                    );

                backupTransform.m_Position.y =
                    sourceTransform.m_Position.y -
                    CraneBackupParkDepth;

                EntityManager.SetComponentData(
                    backup,
                    backupTransform
                );

                if (
                    !EntityManager.HasComponent<Updated>(
                        backup
                    )
                )
                {
                    EntityManager.AddComponent<Updated>(
                        backup
                    );
                }

                visual.CraneBackupEntity =
                    backup;

                ModLog.Checkpoint(
                    "CRANE backup created; building=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; sourceCrane=" +
                    sourceCrane.Index +
                    ":" +
                    sourceCrane.Version +
                    "; backup=" +
                    backup.Index +
                    ":" +
                    backup.Version
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "CRANE backup creation failed; building=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; error=" +
                    ex.GetType().Name +
                    ": " +
                    ex.Message
                );
            }
        }

        private void ParkIneligibleVanillaCrane(
            ConstructionVisual visual,
            Game.Objects.Transform sourceTransform
        )
        {
            Entity crane =
                FindCraneSubObject(
                    visual.Source
                );

            if (
                crane == Entity.Null ||
                !EntityManager.Exists(
                    crane
                ) ||
                !EntityManager.HasComponent<Game.Objects.Transform>(
                    crane
                )
            )
            {
                return;
            }

            Game.Objects.Transform craneTransform =
                EntityManager.GetComponentData<Game.Objects.Transform>(
                    crane
                );

            craneTransform.m_Position.y =
                sourceTransform.m_Position.y -
                CraneBackupParkDepth;

            EntityManager.SetComponentData(
                crane,
                craneTransform
            );

            if (
                !EntityManager.HasComponent<Updated>(
                    crane
                )
            )
            {
                EntityManager.AddComponent<Updated>(
                    crane
                );
            }

            visual.CraneEntity =
                Entity.Null;
        }

        private void DestroyManagedCraneBackup(
            ConstructionVisual visual
        )
        {
            if (
                visual == null ||
                visual.CraneBackupEntity == Entity.Null
            )
            {
                return;
            }

            Entity backup =
                visual.CraneBackupEntity;

            visual.CraneBackupEntity =
                Entity.Null;

            visual.CraneUsingBackup =
                false;

            if (
                !EntityManager.Exists(
                    backup
                )
            )
            {
                return;
            }

            ScheduleNativeProxyDestroy(
                backup
            );
        }

        private static bool TryGetCraneLotCorner(
            ConstructionVisual visual,
            out Vector2 localPosition,
            out int cornerIndex
        )
        {
            localPosition =
                Vector2.zero;

            cornerIndex =
                -1;

            if (
                visual == null ||
                !visual.HasLotDimensions ||
                visual.LotHalfWidth < 1f ||
                visual.LotHalfDepth < 1f ||
                visual.Footprint == null ||
                visual.Footprint.Count < 3
            )
            {
                return false;
            }

            float insetX =
                Mathf.Min(
                    CraneLotEdgeInset,
                    visual.LotHalfWidth *
                    0.20f
                );

            float insetZ =
                Mathf.Min(
                    CraneLotEdgeInset,
                    visual.LotHalfDepth *
                    0.20f
                );

            float edgeX =
                Mathf.Max(
                    0.50f,
                    visual.LotHalfWidth -
                    insetX
                );

            float edgeZ =
                Mathf.Max(
                    0.50f,
                    visual.LotHalfDepth -
                    insetZ
                );

            Vector2[] candidates =
                new Vector2[]
                {
                    new Vector2(
                        -edgeX,
                        -edgeZ
                    ),
                    new Vector2(
                        edgeX,
                        -edgeZ
                    ),
                    new Vector2(
                        edgeX,
                        edgeZ
                    ),
                    new Vector2(
                        -edgeX,
                        edgeZ
                    )
                };

            int startIndex =
                Math.Abs(
                    visual.Source.Index
                ) %
                candidates.Length;

            float bestClearanceSquared =
                float.MinValue;

            for (
                int offset = 0;
                offset < candidates.Length;
                offset++
            )
            {
                int candidateIndex =
                    (
                        startIndex +
                        offset
                    ) %
                    candidates.Length;

                Vector2 candidate =
                    candidates[
                        candidateIndex
                    ];

                if (
                    IsPointInsidePolygon(
                        candidate,
                        visual.Footprint
                    )
                )
                {
                    continue;
                }

                float clearanceSquared =
                    float.MaxValue;

                for (
                    int edgeIndex = 0;
                    edgeIndex < visual.Footprint.Count;
                    edgeIndex++
                )
                {
                    Vector2 edgeStart =
                        visual.Footprint[
                            edgeIndex
                        ];

                    Vector2 edgeEnd =
                        visual.Footprint[
                            (
                                edgeIndex +
                                1
                            ) %
                            visual.Footprint.Count
                        ];

                    clearanceSquared =
                        Mathf.Min(
                            clearanceSquared,
                            DistanceSquaredToSegment(
                                candidate,
                                edgeStart,
                                edgeEnd
                            )
                        );
                }

                if (
                    clearanceSquared >
                    bestClearanceSquared
                )
                {
                    bestClearanceSquared =
                        clearanceSquared;

                    localPosition =
                        candidate;

                    cornerIndex =
                        candidateIndex;
                }
            }

            return
                cornerIndex >= 0;
        }

        private static bool IsPointInsidePolygon(
            Vector2 point,
            List<Vector2> polygon
        )
        {
            bool inside =
                false;

            for (
                int i = 0,
                j = polygon.Count - 1;
                i < polygon.Count;
                j = i++
            )
            {
                Vector2 a =
                    polygon[i];

                Vector2 b =
                    polygon[j];

                bool crosses =
                    (
                        a.y > point.y
                    ) !=
                    (
                        b.y > point.y
                    ) &&
                    point.x <
                    (
                        b.x -
                        a.x
                    ) *
                    (
                        point.y -
                        a.y
                    ) /
                    (
                        b.y -
                        a.y
                    ) +
                    a.x;

                if (
                    crosses
                )
                {
                    inside =
                        !inside;
                }
            }

            return inside;
        }

        private static float DistanceSquaredToSegment(
            Vector2 point,
            Vector2 start,
            Vector2 end
        )
        {
            Vector2 segment =
                end -
                start;

            float segmentLengthSquared =
                segment.sqrMagnitude;

            if (
                segmentLengthSquared < 0.0001f
            )
            {
                return
                    (
                        point -
                        start
                    ).sqrMagnitude;
            }

            float t =
                Mathf.Clamp01(
                    Vector2.Dot(
                        point -
                        start,
                        segment
                    ) /
                    segmentLengthSquared
                );

            Vector2 closest =
                start +
                segment *
                t;

            return
                (
                    point -
                    closest
                ).sqrMagnitude;
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
                0.25f
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
                        segmentLength -
                        0.035f
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

                ConfigureScaffoldRenderer(
                    renderer
                );
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

                ConfigureScaffoldRenderer(
                    renderer
                );
            }

            RemovePrimitiveCollider(
                box
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

            // V1.43.47.4.3.14: the vanilla construction surface is now
            // removed safely in Modification2, so the scaffold can return to
            // HDRP/Lit. This restores material lighting, texture response and
            // dynamic cast/receive shadows without adding any extra renderers.
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
                shader =
                    Shader.Find(
                        "HDRP/Unlit"
                    );
            }

            if (
                shader ==
                null
            )
            {
                ModLog.Info(
                    "V1.43.37 WARNING: no scaffold shader found."
                );

                return;
            }

            ModLog.Checkpoint(
                "SCAFFOLD shader mode; shader=" +
                (
                    shader != null
                        ? shader.name
                        : "null"
                ) +
                "; litPipeline=" +
                (
                    shader != null &&
                    shader.name == "HDRP/Lit"
                )
            );

            Shader buildingShader =
                Shader.Find(
                    "HDRP/Lit"
                );

            if (
                buildingShader ==
                null
            )
            {
                buildingShader =
                    Shader.Find(
                        "HDRP/Unlit"
                    );
            }

            if (
                buildingShader ==
                null
            )
            {
                buildingShader =
                    Shader.Find(
                        "Standard"
                    );
            }

            if (
                buildingShader ==
                null
            )
            {
                buildingShader =
                    shader;
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

            m_BuildingConstructionMaterial =
                new Material(
                    buildingShader
                );

            m_BuildingConstructionMaterial.name =
                "ConstructionAnimation_BuildingFoldMaterial";

            ConfigureOpaqueDepthMaterial(
                m_BuildingConstructionMaterial
            );

            SetMaterialColor(
                m_BuildingConstructionMaterial,
                new UnityEngine.Color(
                    0.66f,
                    0.68f,
                    0.70f,
                    1f
                )
            );

            if (
                m_BuildingConstructionMaterial.HasProperty(
                    "_Metallic"
                )
            )
            {
                m_BuildingConstructionMaterial.SetFloat(
                    "_Metallic",
                    0f
                );
            }

            if (
                m_BuildingConstructionMaterial.HasProperty(
                    "_Smoothness"
                )
            )
            {
                m_BuildingConstructionMaterial.SetFloat(
                    "_Smoothness",
                    0.22f
                );
            }

            ValidateHdrpMaterial(
                m_BuildingConstructionMaterial,
                "building-fold"
            );

            ModLog.Checkpoint(
                "STRUCTURE dedicated material; shader=" +
                (
                    m_BuildingConstructionMaterial.shader != null
                        ? m_BuildingConstructionMaterial.shader.name
                        : "null"
                ) +
                "; queue=" +
                m_BuildingConstructionMaterial.renderQueue
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

            ConfigureScaffoldNoDecals(
                m_ScaffoldMetalMaterial,
                "metal"
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

            ModLog.Checkpoint(
                "SCAFFOLD texture binding; material=metal; baseLoaded=" +
                (m_ScaffoldMetalBaseColorTexture != null) +
                "; unlitMapProperty=" +
                m_ScaffoldMetalMaterial.HasProperty("_UnlitColorMap") +
                "; baseMapProperty=" +
                m_ScaffoldMetalMaterial.HasProperty("_BaseColorMap") +
                "; mainTexProperty=" +
                m_ScaffoldMetalMaterial.HasProperty("_MainTex")
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

            ConfigureScaffoldNoDecals(
                m_ScaffoldDeckMaterial,
                "wood"
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

            ModLog.Checkpoint(
                "SCAFFOLD texture binding; material=wood; baseLoaded=" +
                (m_ScaffoldWoodBaseColorTexture != null) +
                "; unlitMapProperty=" +
                m_ScaffoldDeckMaterial.HasProperty("_UnlitColorMap") +
                "; baseMapProperty=" +
                m_ScaffoldDeckMaterial.HasProperty("_BaseColorMap") +
                "; mainTexProperty=" +
                m_ScaffoldDeckMaterial.HasProperty("_MainTex")
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

            m_CompanyBannerMaterial =
                new Material(
                    shader
                );

            m_CompanyBannerMaterial.name =
                "ConstructionAnimation_CompanyBanner";

            ConfigureOpaqueDepthMaterial(
                m_CompanyBannerMaterial
            );

            ConfigureScaffoldNoDecals(
                m_CompanyBannerMaterial,
                "banner"
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
                        "_UnlitColorMap"
                    )
                )
                {
                    material.SetTexture(
                        "_UnlitColorMap",
                        baseColor
                    );

                    material.SetTextureScale(
                        "_UnlitColorMap",
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

        private static void ConfigureScaffoldNoDecals(
            Material material,
            string label
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            // HDRP Lit materials receive DBuffer decals by default.
            // The vanilla construction ground is rendered through that
            // pipeline, which makes it appear painted over our scaffold.
            // Keep the scaffold opaque/depth-writing, but opt it out of
            // decal reception entirely.
            SetMaterialFloatIfPresent(
                material,
                "_SupportDecals",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_ReceiveDecals",
                0f
            );

            material.EnableKeyword(
                "_DISABLE_DECALS"
            );

            material.DisableKeyword(
                "_ENABLE_DECALS"
            );

            ModLog.Checkpoint(
                "SCAFFOLD no-decals; material=" +
                label +
                "; shader=" +
                (
                    material.shader != null
                        ? material.shader.name
                        : "null"
                ) +
                "; supportDecals=" +
                (
                    material.HasProperty(
                        "_SupportDecals"
                    )
                        ? material.GetFloat(
                            "_SupportDecals"
                        ).ToString(
                            "0.###"
                        )
                        : "n/a"
                )
            );
        }

        private static void ConfigureCutoffDoubleSidedMaterial(
            Material material,
            string label
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return;
            }

            bool hasCullMode =
                material.HasProperty(
                    "_CullMode"
                );

            bool hasCullModeForward =
                material.HasProperty(
                    "_CullModeForward"
                );

            bool hasDoubleSidedEnable =
                material.HasProperty(
                    "_DoubleSidedEnable"
                );

            bool hasDoubleSidedConstants =
                material.HasProperty(
                    "_DoubleSidedConstants"
                );

            if (
                hasCullMode
            )
            {
                material.SetFloat(
                    "_CullMode",
                    (float)UnityEngine.Rendering.CullMode.Off
                );
            }

            if (
                hasCullModeForward
            )
            {
                material.SetFloat(
                    "_CullModeForward",
                    (float)UnityEngine.Rendering.CullMode.Off
                );
            }

            if (
                hasDoubleSidedEnable
            )
            {
                material.SetFloat(
                    "_DoubleSidedEnable",
                    1f
                );
            }

            if (
                hasDoubleSidedConstants
            )
            {
                material.SetVector(
                    "_DoubleSidedConstants",
                    new Vector4(
                        1f,
                        1f,
                        -1f,
                        0f
                    )
                );
            }

            material.doubleSidedGI =
                true;

            material.EnableKeyword(
                "_DOUBLESIDED_ON"
            );

            ModLog.Checkpoint(
                "CUTOFF-DOUBLE-SIDED; label=" +
                label +
                "; shader=" +
                material.shader.name +
                "; cullMode=" +
                hasCullMode +
                "; cullModeForward=" +
                hasCullModeForward +
                "; doubleSidedEnable=" +
                hasDoubleSidedEnable +
                "; doubleSidedConstants=" +
                hasDoubleSidedConstants +
                "; keyword=" +
                material.IsKeywordEnabled(
                    "_DOUBLESIDED_ON"
                )
            );
        }

        private static void ConfigureBuildingNoSnowOverlay(
            Material material,
            string label
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return;
            }

            int changedProperties =
                0;

            int disabledKeywords =
                0;

            try
            {
                Shader shader =
                    material.shader;

                int propertyCount =
                    shader.GetPropertyCount();

                for (
                    int i = 0;
                    i < propertyCount;
                    i++
                )
                {
                    string propertyName =
                        shader.GetPropertyName(
                            i
                        );

                    if (
                        string.IsNullOrEmpty(
                            propertyName
                        ) ||
                        propertyName.IndexOf(
                            "snow",
                            StringComparison.OrdinalIgnoreCase
                        ) < 0
                    )
                    {
                        continue;
                    }

                    UnityEngine.Rendering.ShaderPropertyType propertyType =
                        shader.GetPropertyType(
                            i
                        );

                    int propertyId =
                        shader.GetPropertyNameId(
                            i
                        );

                    try
                    {
                        if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Float ||
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Range
                        )
                        {
                            material.SetFloat(
                                propertyId,
                                0f
                            );

                            changedProperties++;
                        }
                        else if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Color
                        )
                        {
                            material.SetColor(
                                propertyId,
                                new UnityEngine.Color(
                                    0f,
                                    0f,
                                    0f,
                                    0f
                                )
                            );

                            changedProperties++;
                        }
                        else if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Vector
                        )
                        {
                            material.SetVector(
                                propertyId,
                                UnityEngine.Vector4.zero
                            );

                            changedProperties++;
                        }
                    }
                    catch
                    {
                    }
                }

                string[] keywords =
                    material.shaderKeywords;

                if (
                    keywords != null
                )
                {
                    for (
                        int i = 0;
                        i < keywords.Length;
                        i++
                    )
                    {
                        string keyword =
                            keywords[i];

                        if (
                            string.IsNullOrEmpty(
                                keyword
                            ) ||
                            keyword.IndexOf(
                                "snow",
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                        {
                            continue;
                        }

                        material.DisableKeyword(
                            keyword
                        );

                        disabledKeywords++;
                    }
                }
            }
            catch
            {
            }

            ModLog.Checkpoint(
                "BUILDING-FOLD snow overlay disabled; material=" +
                label +
                "; shader=" +
                (
                    material.shader != null
                        ? material.shader.name
                        : "null"
                ) +
                "; properties=" +
                changedProperties +
                "; keywords=" +
                disabledKeywords
            );
        }

        private static void ConfigureBuildingNoCoatOverlay(
            Material material,
            string label
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            bool hadCoatStrength = false;
            float oldCoatStrength = 0f;
            bool changed = false;

            try
            {
                if (
                    material.HasProperty(
                        "_CoatStrength"
                    )
                )
                {
                    hadCoatStrength = true;
                    oldCoatStrength =
                        material.GetFloat(
                            "_CoatStrength"
                        );

                    material.SetFloat(
                        "_CoatStrength",
                        0f
                    );

                    changed = true;
                }

                // The default building shader exposes rust/coating controls
                // alongside _CoatStrength. Zero their transform too so the
                // coating path has no spatial contribution even if the shader
                // evaluates it independently from the strength scalar.
                if (
                    material.HasProperty(
                        "_RustTiling"
                    )
                )
                {
                    material.SetVector(
                        "_RustTiling",
                        UnityEngine.Vector4.zero
                    );
                }

                if (
                    material.HasProperty(
                        "_RustOffset"
                    )
                )
                {
                    material.SetVector(
                        "_RustOffset",
                        UnityEngine.Vector4.zero
                    );
                }
            }
            catch
            {
            }

            ModLog.Checkpoint(
                "BUILDING-FOLD coat overlay disabled; material=" +
                label +
                "; shader=" +
                (
                    material.shader != null
                        ? material.shader.name
                        : "null"
                ) +
                "; hadCoatStrength=" +
                hadCoatStrength +
                "; previous=" +
                oldCoatStrength.ToString(
                    "0.###"
                ) +
                "; changed=" +
                changed
            );
        }

        private void LogBuildingShaderProfileOnce(
            Material material
        )
        {
            if (
                material == null ||
                material.shader == null
            )
            {
                return;
            }

            string shaderName =
                material.shader.name ??
                "null";

            if (
                !m_LoggedBuildingShaderProfiles.Add(
                    shaderName
                )
            )
            {
                return;
            }

            try
            {
                Shader shader =
                    material.shader;

                int propertyCount =
                    shader.GetPropertyCount();

                ModLog.Checkpoint(
                    "BUILDING-SHADER-PROFILE begin; shader=" +
                    shaderName +
                    "; propertyCount=" +
                    propertyCount +
                    "; queue=" +
                    material.renderQueue
                );

                for (
                    int i = 0;
                    i < propertyCount;
                    i++
                )
                {
                    string propertyName =
                        shader.GetPropertyName(
                            i
                        );

                    UnityEngine.Rendering.ShaderPropertyType propertyType =
                        shader.GetPropertyType(
                            i
                        );

                    int propertyId =
                        shader.GetPropertyNameId(
                            i
                        );

                    string value =
                        "<unreadable>";

                    try
                    {
                        if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Float ||
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Range
                        )
                        {
                            value =
                                material.GetFloat(
                                    propertyId
                                ).ToString(
                                    "0.#####"
                                );
                        }
                        else if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Color
                        )
                        {
                            UnityEngine.Color color =
                                material.GetColor(
                                    propertyId
                                );

                            value =
                                color.r.ToString(
                                    "0.###"
                                ) +
                                "," +
                                color.g.ToString(
                                    "0.###"
                                ) +
                                "," +
                                color.b.ToString(
                                    "0.###"
                                ) +
                                "," +
                                color.a.ToString(
                                    "0.###"
                                );
                        }
                        else if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Vector
                        )
                        {
                            UnityEngine.Vector4 vector =
                                material.GetVector(
                                    propertyId
                                );

                            value =
                                vector.x.ToString(
                                    "0.###"
                                ) +
                                "," +
                                vector.y.ToString(
                                    "0.###"
                                ) +
                                "," +
                                vector.z.ToString(
                                    "0.###"
                                ) +
                                "," +
                                vector.w.ToString(
                                    "0.###"
                                );
                        }
                        else if (
                            propertyType ==
                                UnityEngine.Rendering.ShaderPropertyType.Texture
                        )
                        {
                            Texture texture =
                                material.GetTexture(
                                    propertyId
                                );

                            value =
                                texture != null
                                    ? texture.name
                                    : "null";
                        }
                    }
                    catch
                    {
                    }

                    ModLog.Checkpoint(
                        "BUILDING-SHADER-PROFILE property; shader=" +
                        shaderName +
                        "; index=" +
                        i +
                        "; name=" +
                        propertyName +
                        "; type=" +
                        propertyType +
                        "; value=" +
                        value
                    );
                }

                string[] keywords =
                    material.shaderKeywords;

                ModLog.Checkpoint(
                    "BUILDING-SHADER-PROFILE keywords; shader=" +
                    shaderName +
                    "; values=" +
                    (
                        keywords != null &&
                        keywords.Length > 0
                            ? string.Join(
                                ",",
                                keywords
                            )
                            : "<none>"
                    )
                );

                ModLog.Checkpoint(
                    "BUILDING-SHADER-PROFILE end; shader=" +
                    shaderName
                );
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.37 building shader profile failed; shader=" +
                    shaderName +
                    "; error=" +
                    ex.GetType().Name
                );
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

            material.DisableKeyword(
                "_ALPHATEST_ON"
            );

            material.DisableKeyword(
                "_ALPHABLEND_ON"
            );

            material.DisableKeyword(
                "_ALPHAPREMULTIPLY_ON"
            );

            material.DisableKeyword(
                "_BLENDMODE_ALPHA"
            );

            material.DisableKeyword(
                "_BLENDMODE_PRE_MULTIPLY"
            );

            material.DisableKeyword(
                "_BLENDMODE_ADD"
            );

            material.EnableKeyword(
                "_SURFACE_TYPE_OPAQUE"
            );

            SetMaterialFloatIfPresent(
                material,
                "_SurfaceType",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_Surface",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_BlendMode",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_SrcBlend",
                1f
            );

            SetMaterialFloatIfPresent(
                material,
                "_DstBlend",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_AlphaSrcBlend",
                1f
            );

            SetMaterialFloatIfPresent(
                material,
                "_AlphaDstBlend",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_ZWrite",
                1f
            );

            SetMaterialFloatIfPresent(
                material,
                "_ZWriteControl",
                1f
            );

            SetMaterialFloatIfPresent(
                material,
                "_TransparentZWrite",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_ZTest",
                4f
            );

            SetMaterialFloatIfPresent(
                material,
                "_AlphaCutoffEnable",
                0f
            );

            SetMaterialFloatIfPresent(
                material,
                "_AlphaCutoff",
                0f
            );

            ForceMaterialAlphaOne(
                material
            );

            material.SetOverrideTag(
                "RenderType",
                "Opaque"
            );
        }

        private static void SetMaterialFloatIfPresent(
            Material material,
            string propertyName,
            float value
        )
        {
            if (
                material != null &&
                material.HasProperty(
                    propertyName
                )
            )
            {
                material.SetFloat(
                    propertyName,
                    value
                );
            }
        }

        private static void ForceMaterialAlphaOne(
            Material material
        )
        {
            if (
                material == null
            )
            {
                return;
            }

            string[] colorProperties =
                new string[]
                {
                    "_BaseColor",
                    "_Color",
                    "_UnlitColor"
                };

            for (
                int i = 0;
                i < colorProperties.Length;
                i++
            )
            {
                string propertyName =
                    colorProperties[i];

                if (
                    !material.HasProperty(
                        propertyName
                    )
                )
                {
                    continue;
                }

                UnityEngine.Color color =
                    material.GetColor(
                        propertyName
                    );

                color.a =
                    1f;

                material.SetColor(
                    propertyName,
                    color
                );
            }
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
                    "_UnlitColor"
                )
            )
            {
                material.SetColor(
                    "_UnlitColor",
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
                m_BuildingConstructionMaterial != null
            )
            {
                try
                {
                    UnityEngine.Object.Destroy(
                        m_BuildingConstructionMaterial
                    );
                }
                catch
                {
                }

                m_BuildingConstructionMaterial = null;
            }

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
                        $"V1.43.37 source construction surface captured: " +
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
                    $"V1.43.37 continuous source surface suppression failed: {ex}"
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
                        $"V1.43.37 source construction surface restored: " +
                        $"{visual.Source.Index}:{visual.Source.Version}"
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        $"V1.43.37 source surface restoration failed: {ex}"
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

        private bool IsOwnedByConstructionSource(
            Entity ownerEntity,
            Entity constructionSource
        )
        {
            if (
                ownerEntity == Entity.Null ||
                constructionSource == Entity.Null
            )
            {
                return false;
            }

            Entity current =
                ownerEntity;

            for (
                int depth = 0;
                depth < 8;
                depth++
            )
            {
                if (
                    current == constructionSource
                )
                {
                    return true;
                }

                if (
                    current == Entity.Null ||
                    !EntityManager.Exists(
                        current
                    ) ||
                    !EntityManager.HasComponent<Owner>(
                        current
                    )
                )
                {
                    return false;
                }

                Owner owner =
                    EntityManager.GetComponentData<Owner>(
                        current
                    );

                if (
                    owner.m_Owner == current
                )
                {
                    return false;
                }

                current =
                    owner.m_Owner;
            }

            return false;
        }

        private void ProcessQueuedConstructionSandRemoval(
            ConstructionVisual visual
        )
        {
            // V1.43.37: intentionally unused.
            // Sand removal now happens only in HideOwnedConstructionSandAreas
            // and only while Area.Surface is still present.
        }


        private void RetryConstructionSandSurfaceRemoval(
            ConstructionVisual visual
        )
        {
            if (
                visual == null
            )
            {
                return;
            }

            // V1.43.37: keep Surface-only removal, but retry after creation
            // because Sand Surface can appear after the building entity.
            HideOwnedConstructionSandAreas(
                visual
            );
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
                global::UnityEngine.Time.unscaledTime +
                1f;

            if (
                !EntityManager.HasBuffer<Game.Areas.SubArea>(
                    visual.Source
                )
            )
            {
                if (
                    visual.ConstructionSandAreaScanAttempts == 5
                )
                {
                    ModLog.Checkpoint(
                        "CONSTRUCTION-SAND no SubArea buffer after retries; owner=" +
                        visual.Source.Index +
                        ":" +
                        visual.Source.Version
                    );
                }

                return;
            }

            DynamicBuffer<Game.Areas.SubArea> subAreas =
                EntityManager.GetBuffer<Game.Areas.SubArea>(
                    visual.Source
                );

            for (
                int i = 0;
                i < subAreas.Length;
                i++
            )
            {
                Entity areaEntity =
                    subAreas[i].m_Area;

                if (
                    areaEntity == Entity.Null ||
                    !EntityManager.Exists(
                        areaEntity
                    ) ||
                    !EntityManager.HasComponent<PrefabRef>(
                        areaEntity
                    )
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

                SuppressedConstructionSandArea tracked =
                    null;

                for (
                    int trackedIndex = 0;
                    trackedIndex <
                        visual.HiddenConstructionSandAreas.Count;
                    trackedIndex++
                )
                {
                    SuppressedConstructionSandArea candidate =
                        visual.HiddenConstructionSandAreas[
                            trackedIndex
                        ];

                    if (
                        candidate != null &&
                        candidate.Entity ==
                            areaEntity
                    )
                    {
                        tracked =
                            candidate;

                        break;
                    }
                }

                bool hasSurface =
                    EntityManager.HasComponent<Game.Areas.Surface>(
                        areaEntity
                    );

                if (
                    tracked == null
                )
                {
                    tracked =
                        new SuppressedConstructionSandArea
                        {
                            Entity =
                                areaEntity,
                            PrefabName =
                                prefabName,
                            HadSurface =
                                hasSurface,
                            SurfaceRemoved =
                                false
                        };

                    visual.HiddenConstructionSandAreas.Add(
                        tracked
                    );
                }

                // V1.43.47.4.3.14: observation only here. The actual vanilla
                // construction-area suppression runs in ConstructionSandSuppressionSystem
                // during Modification2, before SubAreaReferencesSystem (Modification2B).
                // This avoids changing Area lifecycle state at ModificationEnd.
                tracked.HadSurface =
                    tracked.HadSurface ||
                    hasSurface;

                tracked.SurfaceRemoved =
                    false;

                LogConstructionSandBatchDiagnostic(
                    areaEntity,
                    visual.Source,
                    prefabName,
                    "sand-observed-main-system"
                );
            }

            if (
                visual.HiddenConstructionSandAreas.Count == 0 &&
                visual.ConstructionSandAreaScanAttempts == 5
            )
            {
                ModLog.Checkpoint(
                    "CONSTRUCTION-SAND direct path found no Sand Surface after retries; owner=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version
                );
            }
        }

        private void LogConstructionSandBatchDiagnostic(
            Entity areaEntity,
            Entity constructionSource,
            string prefabName,
            string stage
        )
        {
            if (
                areaEntity == Entity.Null ||
                !EntityManager.Exists(
                    areaEntity
                )
            )
            {
                return;
            }

            try
            {
                bool hasBatch =
                    TryHasConstructionSandBatchByReflection(
                        areaEntity
                    );

                bool hasArea =
                    EntityManager.HasComponent<Game.Areas.Area>(
                        areaEntity
                    );

                bool hasGeometry =
                    EntityManager.HasComponent<Game.Areas.Geometry>(
                        areaEntity
                    );

                bool hasSurface =
                    EntityManager.HasComponent<Game.Areas.Surface>(
                        areaEntity
                    );

                bool hasOwner =
                    EntityManager.HasComponent<Owner>(
                        areaEntity
                    );

                bool hasTemp =
                    EntityManager.HasComponent<Game.Tools.Temp>(
                        areaEntity
                    );

                bool hasDeleted =
                    EntityManager.HasComponent<Deleted>(
                        areaEntity
                    );

                bool hasHidden =
                    EntityManager.HasComponent<Game.Tools.Hidden>(
                        areaEntity
                    );

                bool hasUpdated =
                    EntityManager.HasComponent<Updated>(
                        areaEntity
                    );

                int triangleCount =
                    EntityManager.HasBuffer<Game.Areas.Triangle>(
                        areaEntity
                    )
                        ? EntityManager.GetBuffer<Game.Areas.Triangle>(
                            areaEntity
                        ).Length
                        : -1;

                Entity ownerEntity =
                    Entity.Null;

                if (
                    hasOwner
                )
                {
                    ownerEntity =
                        EntityManager.GetComponentData<Owner>(
                            areaEntity
                        ).m_Owner;
                }

                bool ownerExists =
                    ownerEntity != Entity.Null &&
                    EntityManager.Exists(
                        ownerEntity
                    );

                bool ownerUnderConstruction =
                    ownerExists &&
                    EntityManager.HasComponent<UnderConstruction>(
                        ownerEntity
                    );

                bool ownerHidden =
                    ownerExists &&
                    EntityManager.HasComponent<Game.Tools.Hidden>(
                        ownerEntity
                    );

                bool ownerDeleted =
                    ownerExists &&
                    EntityManager.HasComponent<Deleted>(
                        ownerEntity
                    );

                bool ownerTemp =
                    ownerExists &&
                    EntityManager.HasComponent<Game.Tools.Temp>(
                        ownerEntity
                    );

                string batchIndex =
                    "n/a";

                string metaIndex =
                    "n/a";

                string visibleCount =
                    "n/a";

                string allocatedSize =
                    "n/a";

                string allocation =
                    "n/a";

                if (
                    hasBatch
                )
                {
                    TryReadConstructionSandBatchByReflection(
                        areaEntity,
                        out batchIndex,
                        out metaIndex,
                        out visibleCount,
                        out allocatedSize,
                        out allocation
                    );
                }

                string areaFlags =
                    "n/a";

                if (
                    hasArea
                )
                {
                    Game.Areas.Area area =
                        EntityManager.GetComponentData<Game.Areas.Area>(
                            areaEntity
                        );

                    areaFlags =
                        area.m_Flags.ToString();
                }

                string geometryBounds =
                    "n/a";

                string geometrySurfaceArea =
                    "n/a";

                if (
                    hasGeometry
                )
                {
                    Game.Areas.Geometry geometry =
                        EntityManager.GetComponentData<Game.Areas.Geometry>(
                            areaEntity
                        );

                    geometryBounds =
                        geometry.m_Bounds.ToString();

                    geometrySurfaceArea =
                        geometry.m_SurfaceArea.ToString(
                            "F3"
                        );
                }

                string prefabEntity =
                    "n/a";

                if (
                    EntityManager.HasComponent<PrefabRef>(
                        areaEntity
                    )
                )
                {
                    Entity prefab =
                        EntityManager.GetComponentData<PrefabRef>(
                            areaEntity
                        ).m_Prefab;

                    prefabEntity =
                        prefab.Index +
                        ":" +
                        prefab.Version;
                }

                ModLog.Checkpoint(
                    "SAND-BATCH-DIAG" +
                    "; stage=" +
                    stage +
                    "; entity=" +
                    areaEntity.Index +
                    ":" +
                    areaEntity.Version +
                    "; constructionSource=" +
                    constructionSource.Index +
                    ":" +
                    constructionSource.Version +
                    "; owner=" +
                    ownerEntity.Index +
                    ":" +
                    ownerEntity.Version +
                    "; prefabEntity=" +
                    prefabEntity +
                    "; prefabName=" +
                    (
                        prefabName ??
                        "<null>"
                    ) +
                    "; hasBatch=" +
                    hasBatch +
                    "; batchIndex=" +
                    batchIndex +
                    "; metaIndex=" +
                    metaIndex +
                    "; visibleCount=" +
                    visibleCount +
                    "; allocatedSize=" +
                    allocatedSize +
                    "; allocation=" +
                    allocation +
                    "; triangleCount=" +
                    triangleCount +
                    "; areaFlags=" +
                    areaFlags +
                    "; geometryBounds=" +
                    geometryBounds +
                    "; geometrySurfaceArea=" +
                    geometrySurfaceArea +
                    "; hasSurface=" +
                    hasSurface +
                    "; temp=" +
                    hasTemp +
                    "; deleted=" +
                    hasDeleted +
                    "; hidden=" +
                    hasHidden +
                    "; updated=" +
                    hasUpdated +
                    "; ownerExists=" +
                    ownerExists +
                    "; ownerUnderConstruction=" +
                    ownerUnderConstruction +
                    "; ownerHidden=" +
                    ownerHidden +
                    "; ownerDeleted=" +
                    ownerDeleted +
                    "; ownerTemp=" +
                    ownerTemp
                );
            }
            catch (Exception ex)
            {
                ModLog.Error(
                    "V1.43.46.8 SAND-BATCH-DIAG failed; area=" +
                    areaEntity.Index +
                    ":" +
                    areaEntity.Version +
                    "; stage=" +
                    stage +
                    "; exception=" +
                    ex
                );
            }
        }

        private Type GetConstructionSandBatchRuntimeType()
        {
            try
            {
                return typeof(Game.Areas.Area)
                    .Assembly
                    .GetType(
                        "Game.Areas.Batch",
                        false
                    );
            }
            catch
            {
                return null;
            }
        }

        private MethodInfo FindEntityManagerGenericEntityMethod(
            string methodName
        )
        {
            MethodInfo[] methods =
                typeof(EntityManager).GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public
                );

            for (
                int i = 0;
                i < methods.Length;
                i++
            )
            {
                MethodInfo candidate =
                    methods[i];

                if (
                    candidate.Name != methodName ||
                    !candidate.IsGenericMethodDefinition
                )
                {
                    continue;
                }

                ParameterInfo[] parameters =
                    candidate.GetParameters();

                if (
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(Entity)
                )
                {
                    return candidate;
                }
            }

            return null;
        }

        private bool TryHasConstructionSandBatchByReflection(
            Entity areaEntity
        )
        {
            try
            {
                Type batchType =
                    GetConstructionSandBatchRuntimeType();

                MethodInfo hasComponentMethod =
                    FindEntityManagerGenericEntityMethod(
                        "HasComponent"
                    );

                if (
                    batchType == null ||
                    hasComponentMethod == null
                )
                {
                    return false;
                }

                MethodInfo closedMethod =
                    hasComponentMethod.MakeGenericMethod(
                        batchType
                    );

                object boxedEntityManager =
                    EntityManager;

                object result =
                    closedMethod.Invoke(
                        boxedEntityManager,
                        new object[]
                        {
                            areaEntity
                        }
                    );

                return
                    result is bool &&
                    (bool)result;
            }
            catch (Exception ex)
            {
                ModLog.Info(
                    "V1.43.46.8 SAND-BATCH-DIAG HasComponent reflection failed; " +
                    ex.GetType().Name
                );

                return false;
            }
        }

        private void TryReadConstructionSandBatchByReflection(
            Entity areaEntity,
            out string batchIndex,
            out string metaIndex,
            out string visibleCount,
            out string allocatedSize,
            out string allocation
        )
        {
            batchIndex =
                "reflection-unavailable";

            metaIndex =
                "reflection-unavailable";

            visibleCount =
                "reflection-unavailable";

            allocatedSize =
                "reflection-unavailable";

            allocation =
                "reflection-unavailable";

            try
            {
                Type batchType =
                    GetConstructionSandBatchRuntimeType();

                MethodInfo getComponentDataMethod =
                    FindEntityManagerGenericEntityMethod(
                        "GetComponentData"
                    );

                if (
                    batchType == null ||
                    getComponentDataMethod == null
                )
                {
                    return;
                }

                MethodInfo closedMethod =
                    getComponentDataMethod.MakeGenericMethod(
                        batchType
                    );

                object boxedEntityManager =
                    EntityManager;

                object boxedBatch =
                    closedMethod.Invoke(
                        boxedEntityManager,
                        new object[]
                        {
                            areaEntity
                        }
                    );

                if (
                    boxedBatch == null
                )
                {
                    return;
                }

                batchIndex =
                    ReadReflectedFieldAsString(
                        batchType,
                        boxedBatch,
                        "m_BatchIndex"
                    );

                metaIndex =
                    ReadReflectedFieldAsString(
                        batchType,
                        boxedBatch,
                        "m_MetaIndex"
                    );

                visibleCount =
                    ReadReflectedFieldAsString(
                        batchType,
                        boxedBatch,
                        "m_VisibleCount"
                    );

                allocatedSize =
                    ReadReflectedFieldAsString(
                        batchType,
                        boxedBatch,
                        "m_AllocatedSize"
                    );

                allocation =
                    ReadReflectedFieldAsString(
                        batchType,
                        boxedBatch,
                        "m_BatchAllocation"
                    );
            }
            catch (Exception ex)
            {
                batchIndex =
                    "reflection-error";

                metaIndex =
                    "reflection-error";

                visibleCount =
                    "reflection-error";

                allocatedSize =
                    "reflection-error";

                allocation =
                    "reflection-error";

                ModLog.Info(
                    "V1.43.46.8 SAND-BATCH-DIAG Batch reflection failed; " +
                    ex.GetType().Name
                );
            }
        }

        private static string ReadReflectedFieldAsString(
            Type declaringType,
            object instance,
            string fieldName
        )
        {
            try
            {
                FieldInfo field =
                    declaringType.GetField(
                        fieldName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                if (
                    field == null
                )
                {
                    return "field-not-found";
                }

                object value =
                    field.GetValue(
                        instance
                    );

                return value != null
                    ? value.ToString()
                    : "null";
            }
            catch
            {
                return "field-read-error";
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
                SuppressedConstructionSandArea tracked =
                    visual.HiddenConstructionSandAreas[i];

                if (
                    tracked == null ||
                    !tracked.HadSurface ||
                    !tracked.SurfaceRemoved
                )
                {
                    continue;
                }

                Entity areaEntity =
                    tracked.Entity;

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

                    ModLog.Checkpoint(
                        "CONSTRUCTION-SAND surface-only restore requested; area=" +
                        areaEntity.Index +
                        ":" +
                        areaEntity.Version
                    );
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        "CONSTRUCTION-SAND surface-only restore failed: " +
                        ex
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

            Entity diagnosticSource =
                visual.Source;

            SetDiagnosticStage(
                "destroy.visual",
                diagnosticSource
            );

            ModLog.Checkpoint(
                "DESTROY visual begin; source=" +
                diagnosticSource.Index +
                ":" +
                diagnosticSource.Version +
                "; proxy=" +
                visual.Proxy.Index +
                ":" +
                visual.Proxy.Version +
                "; meshes=" +
                visual.ScaffoldMeshes.Count
            );

            RestoreOwnedConstructionSandAreas(
                visual
            );

            ClearPublishedTerrainDirt(
                visual
            );

            RestoreVanillaSandSurfaces(
                visual
            );

            DestroyFoldedBuildingVisual(
                visual
            );

            DestroyScaffold(
                visual
            );

            DestroyManagedCraneBackup(
                visual
            );

            ScheduleNativeProxyDestroy(
                visual.Proxy
            );

            visual.Proxy =
                Entity.Null;

            visual.Source =
                Entity.Null;

            ModLog.Checkpoint(
                "DESTROY visual complete; source=" +
                diagnosticSource.Index +
                ":" +
                diagnosticSource.Version
            );
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

            bool hadScaffoldResources =
                visual.ScaffoldRoot != null ||
                visual.ScaffoldMeshes.Count > 0;

            if (
                hadScaffoldResources
            )
            {
                ModLog.Checkpoint(
                    "SCAFFOLD destroy begin; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version +
                    "; meshes=" +
                    visual.ScaffoldMeshes.Count +
                    "; levels=" +
                    visual.ScaffoldLevels.Count
                );
            }

            for (
                int i = 0;
                i < visual.ScaffoldMeshes.Count;
                i++
            )
            {
                Mesh scaffoldMesh =
                    visual.ScaffoldMeshes[i];

                if (
                    scaffoldMesh ==
                    null
                )
                {
                    continue;
                }

                ScheduleUnityDestroy(
                    scaffoldMesh
                );
            }

            visual.ScaffoldMeshes.Clear();

            if (
                visual.ScaffoldRenderers != null
            )
            {
                visual.ScaffoldRenderers.Clear();
            }

            if (
                visual.ScaffoldRoot !=
                null
            )
            {
                try
                {
                    visual.ScaffoldRoot.SetActive(
                        false
                    );
                }
                catch
                {
                }

                ScheduleUnityDestroy(
                    visual.ScaffoldRoot
                );
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

            visual.ScaffoldDistanceVisible =
                true;

            visual.NextScaffoldDistanceCheckTime =
                0f;

            visual.ScaffoldFullyRevealedCount =
                0;

            visual.ScaffoldPartialRevealIndex =
                -1;

            visual.ScaffoldFullyDismantledCount =
                0;

            visual.ScaffoldPartialDismantleIndex =
                -1;

            visual.ScaffoldShadowsEnabled =
                true;

            if (
                hadScaffoldResources
            )
            {
                ModLog.Checkpoint(
                    "SCAFFOLD destroy complete; source=" +
                    visual.Source.Index +
                    ":" +
                    visual.Source.Version
                );
            }
        }

        private void ScheduleUnityDestroy(
            UnityEngine.Object unityObject
        )
        {
            if (
                unityObject ==
                null
            )
            {
                return;
            }

            m_PendingUnityDestroys.Add(
                new PendingUnityDestroy
                {
                    Object = unityObject,
                    Frames = UnityDestroyDelayFrames
                }
            );
        }

        private void ProcessPendingUnityDestroys()
        {
            for (
                int i = m_PendingUnityDestroys.Count - 1;
                i >= 0;
                i--
            )
            {
                PendingUnityDestroy pending =
                    m_PendingUnityDestroys[i];

                if (
                    pending == null
                )
                {
                    m_PendingUnityDestroys.RemoveAt(
                        i
                    );
                    continue;
                }

                pending.Frames--;

                if (
                    pending.Frames > 0
                )
                {
                    continue;
                }

                try
                {
                    if (
                        pending.Object !=
                        null
                    )
                    {
                        UnityEngine.Object.Destroy(
                            pending.Object
                        );
                    }
                }
                catch
                {
                }

                m_PendingUnityDestroys.RemoveAt(
                    i
                );
            }
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
                ModLog.Checkpoint(
                    "PROXY schedule-destroy; proxy=" +
                    proxy.Index +
                    ":" +
                    proxy.Version
                );

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
                        ModLog.Checkpoint(
                            "PROXY destroy begin; proxy=" +
                            proxy.Index +
                            ":" +
                            proxy.Version +
                            "; delayedFrames=" +
                            pending.Frames
                        );

                        EntityManager.DestroyEntity(
                            proxy
                        );

                        ModLog.Checkpoint(
                            "PROXY destroy end; proxy=" +
                            proxy.Index +
                            ":" +
                            proxy.Version
                        );
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Error(
                        "V1.43.37 proxy destroy managed exception; proxy=" +
                        proxy.Index +
                        ":" +
                        proxy.Version +
                        "; exception=" +
                        ex
                    );
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
                RestoreOwnedConstructionSandAreas(
                    visual
                );

                ClearPublishedTerrainDirt(
                    visual
                );

                RestoreVanillaSandSurfaces(
                    visual
                );

                DestroyFoldedBuildingVisual(
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

            for (
                int i = 0;
                i < m_PendingUnityDestroys.Count;
                i++
            )
            {
                try
                {
                    PendingUnityDestroy pending =
                        m_PendingUnityDestroys[i];

                    if (
                        pending != null &&
                        pending.Object != null
                    )
                    {
                        UnityEngine.Object.Destroy(
                            pending.Object
                        );
                    }
                }
                catch
                {
                }
            }

            m_PendingUnityDestroys.Clear();

            DestroyScaffoldMaterials();

            base.OnDestroy();
        }
    }

    // V1.43.47.4.3.14: dedicated lifecycle-safe suppression system.
    // It deliberately runs in Modification2. The vanilla
    // SubAreaReferencesSystem runs immediately afterwards in Modification2B,
    // so deleted construction Area entities are detached from their owner
    // before rendering reaches AreaBatchSystem in PreCulling.
    public sealed partial class ConstructionSandSuppressionSystem : GameSystemBase
    {
        private EntityQuery m_BuildingQuery;
        private PrefabSystem m_PrefabSystem;
        private readonly HashSet<Entity> m_LoggedAreas =
            new HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem =
                World.GetOrCreateSystemManaged<PrefabSystem>();

            m_BuildingQuery =
                GetEntityQuery(
                    ComponentType.ReadOnly<UnderConstruction>(),
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>()
                );

            RequireForUpdate(
                m_BuildingQuery
            );

            ModLog.Checkpoint(
                "CONSTRUCTION-SAND suppression system ready; phase=Modification2; version=V1.43.47.4.3.14"
            );
        }

        protected override void OnUpdate()
        {
            NativeArray<Entity> buildings =
                m_BuildingQuery.ToEntityArray(
                    Allocator.Temp
                );

            try
            {
                for (
                    int buildingIndex = 0;
                    buildingIndex < buildings.Length;
                    buildingIndex++
                )
                {
                    Entity building =
                        buildings[buildingIndex];

                    if (
                        building == Entity.Null ||
                        !EntityManager.Exists(
                            building
                        ) ||
                        !EntityManager.HasBuffer<Game.Areas.SubArea>(
                            building
                        )
                    )
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.SubArea> subAreas =
                        EntityManager.GetBuffer<Game.Areas.SubArea>(
                            building
                        );

                    // Copy targets first. Adding Deleted is a structural change;
                    // never mutate ECS structure while walking the live buffer.
                    List<Entity> targets =
                        null;

                    for (
                        int i = 0;
                        i < subAreas.Length;
                        i++
                    )
                    {
                        Entity area =
                            subAreas[i].m_Area;

                        if (
                            area == Entity.Null ||
                            !EntityManager.Exists(
                                area
                            ) ||
                            EntityManager.HasComponent<Deleted>(
                                area
                            ) ||
                            !EntityManager.HasComponent<PrefabRef>(
                                area
                            ) ||
                            !EntityManager.HasComponent<Game.Areas.Area>(
                                area
                            )
                        )
                        {
                            continue;
                        }

                        PrefabRef prefabRef =
                            EntityManager.GetComponentData<PrefabRef>(
                                area
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
                            targets == null
                        )
                        {
                            targets =
                                new List<Entity>();
                        }

                        targets.Add(
                            area
                        );
                    }

                    if (
                        targets == null
                    )
                    {
                        continue;
                    }

                    for (
                        int i = 0;
                        i < targets.Count;
                        i++
                    )
                    {
                        Entity area =
                            targets[i];

                        if (
                            !EntityManager.Exists(
                                area
                            ) ||
                            EntityManager.HasComponent<Deleted>(
                                area
                            )
                        )
                        {
                            continue;
                        }

                        bool hadBatch =
                            EntityManager.HasComponent<Game.Areas.Batch>(
                                area
                            );

                        bool hadSurface =
                            EntityManager.HasComponent<Game.Areas.Surface>(
                                area
                            );

                        EntityManager.AddComponent<Deleted>(
                            area
                        );

                        if (
                            m_LoggedAreas.Add(
                                area
                            )
                        )
                        {
                            ModLog.Checkpoint(
                                "CONSTRUCTION-SAND ordered delete; phase=Modification2; owner=" +
                                building.Index +
                                ":" +
                                building.Version +
                                "; area=" +
                                area.Index +
                                ":" +
                                area.Version +
                                "; hadBatch=" +
                                hadBatch +
                                "; hadSurface=" +
                                hadSurface
                            );
                        }
                    }
                }
            }
            finally
            {
                buildings.Dispose();
            }
        }
    }

}
