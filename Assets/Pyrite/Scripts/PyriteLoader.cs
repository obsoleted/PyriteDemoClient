﻿namespace Pyrite
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Extensions;
    using Microsoft.Xna.Framework;
    using Model;
    using UnityEngine;

    public class PyriteLoader : MonoBehaviour
    {
        private DictionaryCache<string, GeometryBuffer> _eboCache;

        private readonly Dictionary<string, MaterialData> _partiallyConstructedMaterialDatas =
            new Dictionary<string, MaterialData>();

        // Debug Text Counters
        private readonly GUIStyle _guiStyle = new GUIStyle();
        private int EboCacheHits;
        private int EboCacheMisses;
        private int MaterialCacheHits;
        private int MaterialCacheMisses;

        private int FileCacheHits;
        private int FileCacheMisses;

        // Requests that were cancelled between queues
        private int CancelledRequests;
        // Requests that were cancelled by the time the cache tried to load it
        private int LateCancelledRequests;
        // End counter bits

        private const int RETRY_LIMIT = 2;

        private float _geometryBufferAltitudeTransform;

        [Tooltip("If set object will be moved closer to model being loaded")]
        public GameObject CameraRig;

        public bool EnableDebugLogs = false;

        [Tooltip("Prefab for detection cubes")]
        public GameObject PlaceHolderCube;

        [Tooltip("Prefab for base cube object that we will populate data")]
        public GameObject BaseModelCube;

        [Header("Server Options")]
        public string PyriteServer;

        [Header("Set Options (required)")]
        public int DetailLevel = 6;

        public bool FilterDetailLevels = false;
        public List<int> DetailLevelsToFilter;
        public GeometryBuffer.ModelFormat ModelFormat = GeometryBuffer.ModelFormat.Ebo;
        public string ModelVersion = "V2";
        public string SetName;

        [Header("Performance options")]
        public int EboCacheSize = 250;

        public int MaterialDataCacheSize = 25;

        [Tooltip("Sets how many cubes can be built per frame (0 for No Limit)")]
        public int CubeBuildLimitPerFrame = 0;

        [Tooltip("Sets how many textures will be requested per frame (0 for No Limit)")]
        public int MaterialRequestLimitPerFrame = 0;

        [Tooltip("Sets how many models will be requested per frame (0 for No Limit)")]
        public int ModelRequestLimitPerFrame = 0;

        [Header("Debug Options")]
        public bool UseCameraDetection = true;

        public bool UseUnlitShader = true;
        public bool UseFileCache = true;
        public bool ShowDebugText = true;

        [Header("Other Options")]
        public float UpgradeFactor = 1.05f;

        public float UpgradeConstant = 0.0f;
        public float DowngradeFactor = 1.05f;
        public float DowngradeConstant = 0.0f;
        public bool UseWwwForTextures = false;
        public bool UseWwwForEbo = false;
        public bool CacheFill = false;
        public int CacheSize = 3000;

        [Tooltip("If set will bet used as the address for the proxy used for web requests")]
        public string ProxyUrl;

        [HideInInspector]
        public Plane[] CameraFrustrum;

        [HideInInspector]
        public MaterialDataCache MaterialDataCache { get; private set; }

        // Queue for requests that are waiting for their material data
        private readonly Queue<LoadCubeRequest> _loadMaterialQueue = new Queue<LoadCubeRequest>(10);

        // Queue textures that have been downloaded and now need to be constructed into material data
        private readonly Queue<KeyValuePair<string, byte[]>> _texturesReadyForMaterialDataConstruction =
            new Queue<KeyValuePair<string, byte[]>>(5);

        // Dictionary list to keep track of requests that are dependent on some other in progress item (e.g. material data or model data loading)
        private readonly Dictionary<string, LinkedList<LoadCubeRequest>> _dependentCubes =
            new Dictionary<string, LinkedList<LoadCubeRequest>>();

        // Queue for requests that have material data but need model data
        private readonly Queue<LoadCubeRequest> _loadGeometryBufferQueue = new Queue<LoadCubeRequest>(10);

        // Queue for requests that have model and material data and so are ready for construction
        private readonly Queue<LoadCubeRequest> _buildCubeRequests = new Queue<LoadCubeRequest>(10);
#if !UNITY_WSA
        private static Thread _mainThread;
#endif

        protected bool Loaded { get; private set; }

        private PyriteQuery _pyriteQuery;
        private PyriteSetVersionDetailLevel _pyriteLevel;

        private string ModelFormatString;

        private void Start()
        {
            if (string.IsNullOrEmpty(SetName))
            {
                Debug.LogError("Must specify SetName");
                return;
            }

            if (string.IsNullOrEmpty(ModelVersion))
            {
                Debug.LogError("Must specify ModelVersion");
                return;
            }

            ModelFormatString = ModelFormat.ToString();

            InternalSetup();
            StartCoroutine(InternalLoad());
        }

        private IEnumerator InternalLoad()
        {
            yield return StartCoroutine(Load());
            Loaded = true;
        }

        private void InternalSetup()
        {
#if !UNITY_WSA
            _mainThread = Thread.CurrentThread;
#endif

            _guiStyle.normal.textColor = Color.red;

            if (_eboCache == null)
            {
                _eboCache = new DictionaryCache<string, GeometryBuffer>(EboCacheSize);
            }
            else
            {
                Debug.LogWarning("Ebo cache already initialized. Skipping initizliation.");
            }

            if (MaterialDataCache == null)
            {
                MaterialDataCache = new MaterialDataCache(MaterialDataCacheSize);
            }
            else
            {
                Debug.LogWarning("Material Data cache  already initialized. Skipping initizliation.");
            }

            ObjectPooler.Current.CreatePoolForObject(BaseModelCube);

            // Optional pool only used in camera detection scenario
            if (PlaceHolderCube != null)
            {
                ObjectPooler.Current.CreatePoolForObject(PlaceHolderCube);
            }

            CacheWebRequest.InitializeCache(CacheSize, ProxyUrl);
        }

        private static bool CheckThread(bool expectMainThread)
        {
#if !UNITY_WSA
            var asExpected = expectMainThread != _mainThread.Equals(Thread.CurrentThread);
            if (asExpected)
            {
                Debug.LogWarning("Warning unexpected thread. Expected: " + expectMainThread);
            }
            return asExpected;
#else
    // We do not get Thread class in Windows Store Apps so just return true
            return true;
#endif
        }

        private static bool CheckIfMainThread()
        {
            return CheckThread(true);
        }

        private static bool CheckIfBackgroundThread()
        {
            return CheckThread(false);
        }

        private void Update()
        {
            if (!Loaded)
            {
                return;
            }

            // Update camera frustrum
            if (UseCameraDetection)
                CameraFrustrum = GeometryUtility.CalculateFrustumPlanes(Camera.main);

            // Check for work in Update
            ProcessQueues();
        }

        // Look through all work queues starting any work that is needed
        private void ProcessQueues()
        {
            // Look for requests that are ready to be constructed
            ProcessQueue(_buildCubeRequests, BuildCubeRequest, CubeBuildLimitPerFrame);

            // Look for textures that have been downloaded and need to be converted to MaterialData
            if (Monitor.TryEnter(_texturesReadyForMaterialDataConstruction))
            {
                while (_texturesReadyForMaterialDataConstruction.Count > 0)
                {
                    var materialDataKeyTextureBytesPair = _texturesReadyForMaterialDataConstruction.Dequeue();
                    StartCoroutine(FinishCreatingMaterialDataWithTexture(materialDataKeyTextureBytesPair));
                }
                Monitor.Exit(_texturesReadyForMaterialDataConstruction);
            }
            // Look for requests that need material data set
            ProcessQueue(_loadMaterialQueue, GetMaterialForRequest, MaterialRequestLimitPerFrame);

            // Look for requests that need geometry buffer (model data)
            ProcessQueue(_loadGeometryBufferQueue, GetModelForRequest, ModelRequestLimitPerFrame);
        }

        // Helper for locking a queue, pulling off requests and invoking a handler function for them
        private void ProcessQueue(Queue<LoadCubeRequest> queue, Func<LoadCubeRequest, IEnumerator> requestProcessFunc,
            int limit)
        {
            var noLimit = limit == 0;
            if (Monitor.TryEnter(queue))
            {
                while (queue.Count > 0 && (noLimit || (limit-- > 0)))
                {
                    var request = queue.Dequeue();
                    if (!request.Cancelled)
                    {
                        StartCoroutine(requestProcessFunc(request));
                    }
                    else
                    {
                        lock (MaterialDataCache)
                        {
                            if (request.MaterialData != null && request.MaterialData.DiffuseTex != null)
                            {
                                MaterialDataCache.Release(request.MaterialData.DiffuseTex.name);
                            }
                        }
                        CancelledRequests++;
                    }
                }
                Monitor.Exit(queue);
            }
        }

        /// <summary>
        /// Returns whether or not any requests are still active (not cancelled) for the provided dependency
        /// </summary>
        /// <param name="dependencyKey">Dependency we want to check for</param>
        /// <returns>true if any dependent requests are not cancelled, false if that is not the case</returns>
        public bool DependentRequestsExistBlocking(string dependencyKey)
        {
            CheckIfBackgroundThread();
            lock (_dependentCubes)
            {
                LinkedList<LoadCubeRequest> dependentRequests;
                if (_dependentCubes.TryGetValue(dependencyKey, out dependentRequests))
                {
                    if (dependentRequests.Any(request => !request.Cancelled))
                    {
                        return true;
                    }
                    // No dependent requests still active, delete the list
                    LateCancelledRequests++;
                    _dependentCubes.Remove(dependencyKey);
                }
                else
                {
                    Debug.LogError("Should not be possible...");
                }
            }
            return false;
        }

        private IEnumerator AddDependentRequest(LoadCubeRequest dependentRequest, string dependencyKey)
        {
            // Model is in the process of being constructed. Add request to dependency list
            while (!Monitor.TryEnter(_dependentCubes))
            {
                yield return null;
            }
            LinkedList<LoadCubeRequest> dependentRequests;
            if (!_dependentCubes.TryGetValue(dependencyKey, out dependentRequests))
            {
                dependentRequests = new LinkedList<LoadCubeRequest>();
                _dependentCubes.Add(dependencyKey, dependentRequests);
            }
            dependentRequests.AddLast(dependentRequest);
            Monitor.Exit(_dependentCubes);
        }

#if UNITY_EDITOR
        private void OnGUI()
        {
            if (ShowDebugText) // or check the app debug flag
            {
                if (EboCacheHits + EboCacheMisses > 1000)
                {
                    EboCacheMisses = EboCacheHits = 0;
                }

                if (MaterialCacheHits + MaterialCacheMisses > 1000)
                {
                    MaterialCacheMisses = MaterialCacheHits = 0;
                }

                const int yOffset = 10;
                string caches;

                if (UseFileCache)
                {
                    caches =
                        string.Format("Mesh {0}/{1} ({2}) Mat {3}/{4} ({5}|{6}) File {7}/{8} Cr {9} Lcr {10} Dr {11}",
                            EboCacheHits,
                            EboCacheMisses,
                            _eboCache.Count,
                            MaterialCacheHits,
                            MaterialCacheMisses,
                            MaterialDataCache.Count,
                            MaterialDataCache.Evictions,
                            FileCacheHits,
                            FileCacheMisses,
                            CancelledRequests,
                            LateCancelledRequests,
                            _dependentCubes.Count);
                }
                else
                {
                    caches = string.Format("Mesh {0}/{1} Mat {2}/{3} Cr {4} Lcr {5}",
                        EboCacheHits,
                        EboCacheMisses,
                        MaterialCacheHits,
                        MaterialCacheMisses,
                        CancelledRequests,
                        LateCancelledRequests);
                }

                GUI.Label(new Rect(10, yOffset, 200, 50), caches, _guiStyle);
            }
        }
#endif

        private PyriteCube CreateCubeFromCubeBounds(CubeBounds cubeBounds)
        {
            return new PyriteCube
            {
                X = (int) cubeBounds.BoundingBox.Min.x,
                Y = (int) cubeBounds.BoundingBox.Min.y,
                Z = (int) cubeBounds.BoundingBox.Min.z
            };
        }

        protected virtual IEnumerator Load()
        {
            _pyriteQuery = new PyriteQuery(this, SetName, ModelVersion, PyriteServer, UpgradeFactor, UpgradeConstant,
                DowngradeFactor, DowngradeConstant);
            yield return StartCoroutine(_pyriteQuery.LoadAll(FilterDetailLevels ? DetailLevelsToFilter : null));
            var initialDetailLevelIndex = DetailLevel - 1;
            if (UseCameraDetection)
            {
                initialDetailLevelIndex = _pyriteQuery.DetailLevels.Length - 1;
            }

            _pyriteLevel = _pyriteQuery.DetailLevels[initialDetailLevelIndex];

            var allOctCubes = _pyriteQuery.DetailLevels[initialDetailLevelIndex].Octree.AllItems();
            foreach (var octCube in allOctCubes)
            {
                var pCube = CreateCubeFromCubeBounds(octCube);
                var x = pCube.X;
                var y = pCube.Y;
                var z = pCube.Z;
                var cubePos = _pyriteLevel.GetWorldCoordinatesForCube(pCube);
                _geometryBufferAltitudeTransform = 0 - _pyriteLevel.ModelBoundsMin.z;

                if (UseCameraDetection)
                {
                    var detectionCube = ObjectPooler.Current.GetPooledObject(PlaceHolderCube);
                    detectionCube.transform.position = new Vector3(-cubePos.x,
                        cubePos.z + _geometryBufferAltitudeTransform, -cubePos.y);
                    detectionCube.transform.rotation = Quaternion.identity;
                    var meshRenderer = detectionCube.GetComponent<MeshRenderer>();
                    meshRenderer.enabled = true;
                    detectionCube.GetComponent<IsRendered>()
                        .SetCubePosition(x, y, z, initialDetailLevelIndex, _pyriteQuery, this);

                    detectionCube.transform.localScale = new Vector3(
                        _pyriteLevel.WorldCubeScale.x,
                        _pyriteLevel.WorldCubeScale.z,
                        _pyriteLevel.WorldCubeScale.y);

                    detectionCube.SetActive(true);
                }
                else
                {
                    var loadRequest = new LoadCubeRequest(x, y, z, initialDetailLevelIndex, _pyriteQuery, null);
                    yield return StartCoroutine(EnqueueLoadCubeRequest(loadRequest));
                }
            }

            if (CameraRig != null)
            {
                // Hardcodes the coordinate inversions which are parameterized on the geometry buffer

                var min = new Vector3(
                    -_pyriteLevel.ModelBoundsMin.x,
                    _pyriteLevel.ModelBoundsMin.z + _geometryBufferAltitudeTransform,
                    -_pyriteLevel.ModelBoundsMin.y);
                var max = new Vector3(
                    -_pyriteLevel.ModelBoundsMax.x,
                    _pyriteLevel.ModelBoundsMax.z + _geometryBufferAltitudeTransform,
                    -_pyriteLevel.ModelBoundsMax.y);

                var newCameraPosition = min + (max - min) / 2.0f;
                newCameraPosition += new Vector3(0, (max - min).y * 1.4f, 0);
                CameraRig.transform.position = newCameraPosition;
                CameraRig.transform.rotation = Quaternion.Euler(0, 180, 0);

                //Kainiemi: Some mechanism needed to inform InputManager about the transform change
                var inputManager = CameraRig.GetComponent<PyriteDemoClient.InputManager>();
                if (inputManager != null)
                {
                    // Give input manager position limits based on model bounds
                    var highestLod = _pyriteQuery.DetailLevels.First();
                    var lowestLod = _pyriteQuery.DetailLevels.Last();
                    inputManager.SetInputLimits(
                        new Vector3(highestLod.ModelBoundsMin.x + lowestLod.WorldCubeScale.x / 2,
                            highestLod.ModelBoundsMin.z + _geometryBufferAltitudeTransform +
                            lowestLod.WorldCubeScale.z / 8,
                            highestLod.ModelBoundsMin.y + lowestLod.WorldCubeScale.y / 2),
                        new Vector3(highestLod.ModelBoundsMax.x - lowestLod.WorldCubeScale.x / 2,
                            highestLod.ModelBoundsMax.z + _geometryBufferAltitudeTransform +
                            (lowestLod.WorldCubeScale.z * 1.5f),
                            highestLod.ModelBoundsMax.y - lowestLod.WorldCubeScale.y / 2));

                    inputManager.NotifyOnTransformChange();
                }
            }
        }

        public IEnumerator AddUpgradedDetectorCubes(PyriteQuery pyriteQuery, int x, int y, int z, int lod,
            Action<IEnumerable<GameObject>> registerCreatedDetectorCubes)
        {
            var newLod = lod - 1;
            var createdDetectors = new List<GameObject>();
            var pyriteLevel = pyriteQuery.DetailLevels[newLod];

            var cubeFactor = pyriteQuery.GetNextCubeFactor(lod);
            var min = new Vector3(x * (int) cubeFactor.x + 0.5f, y * (int) cubeFactor.y + 0.5f,
                z * (int) cubeFactor.z + 0.5f);
            var max = new Vector3((x + 1) * (int) cubeFactor.x - 0.5f, (y + 1) * (int) cubeFactor.y - 0.5f,
                (z + 1) * (int) cubeFactor.z - 0.5f);
            var intersections =
                pyriteQuery.DetailLevels[newLod].Octree.AllIntersections(new BoundingBox {Min = min, Max = max});
            foreach (var i in intersections)
            {
                var newCube = CreateCubeFromCubeBounds(i.Object);
                var cubePos = pyriteLevel.GetWorldCoordinatesForCube(newCube);

                var newDetectionCube = ObjectPooler.Current.GetPooledObject(PlaceHolderCube);
                newDetectionCube.transform.position = new Vector3(-cubePos.x,
                    cubePos.z + _geometryBufferAltitudeTransform, -cubePos.y);
                newDetectionCube.transform.rotation = Quaternion.identity;
                var meshRenderer = newDetectionCube.GetComponent<MeshRenderer>();
                meshRenderer.enabled = true;
                newDetectionCube.GetComponent<IsRendered>()
                    .SetCubePosition(newCube.X, newCube.Y, newCube.Z, newLod, pyriteQuery, this);

                newDetectionCube.transform.localScale = new Vector3(
                    pyriteLevel.WorldCubeScale.x,
                    pyriteLevel.WorldCubeScale.z,
                    pyriteLevel.WorldCubeScale.y);
                newDetectionCube.SetActive(true);
                createdDetectors.Add(newDetectionCube);
            }
            registerCreatedDetectorCubes(createdDetectors);
            yield break;
        }

        // Used to initiate request to load and display a cube in the scene
        public IEnumerator EnqueueLoadCubeRequest(LoadCubeRequest loadRequest)
        {
            yield return StartCoroutine(_loadMaterialQueue.ConcurrentEnqueue(loadRequest));
        }

        // Invoked when a load requeset has failed to download the model data it needs
        // Requests are retried RETRY_LIMIT times if they fail more than that the request is abandoned (error is logged)
        // When this happens if any dependent cubes want the resource that failed one request is re-queued to try again (under that requests Retry quota)
        private void FailGetGeometryBufferRequest(LoadCubeRequest loadRequest, string modelPath)
        {
            CheckIfBackgroundThread();
            loadRequest.Failures++;
            lock (_eboCache)
            {
                // Remove the 'in progress' marker from the cache
                _eboCache.Remove(modelPath);
            }

            if (RETRY_LIMIT > loadRequest.Failures)
            {
                Debug.LogError("Retry limit hit for: " + modelPath);
                Debug.LogError("Cube load failed for " + loadRequest);

                // Let another depenent cube try
                lock (_dependentCubes)
                {
                    LinkedList<LoadCubeRequest> dependentRequests;
                    if (_dependentCubes.TryGetValue(modelPath, out dependentRequests))
                    {
                        var request = dependentRequests.Last.Value;
                        dependentRequests.RemoveLast();
                        _loadGeometryBufferQueue.ConcurrentEnqueue(request).Wait();
                    }
                }
            }
            else
            {
                // Queue for retry
                _loadGeometryBufferQueue.ConcurrentEnqueue(loadRequest).Wait();
            }
        }

        // Invoked when a load requeset has failed to download the material data it needs
        // Requests are retried RETRY_LIMIT times if they fail more than that the request is abandoned (error is logged)
        // When this happens if any dependent cubes want the resource that failed one request is re-queued to try again (under that request's Retry quota)
        private void FailGetMaterialDataRequest(LoadCubeRequest loadRequest, string materialPath)
        {
            CheckIfBackgroundThread();
            loadRequest.Failures++;
            lock (MaterialDataCache)
            {
                // Remove the 'in progress' marker from the cache
                MaterialDataCache.Remove(materialPath);
            }

            if (RETRY_LIMIT > loadRequest.Failures)
            {
                Debug.LogError("Retry limit hit for: " + materialPath);
                Debug.LogError("Cube load failed for " + loadRequest);

                lock (_dependentCubes)
                {
                    LinkedList<LoadCubeRequest> dependentRequests;
                    if (_dependentCubes.TryGetValue(materialPath, out dependentRequests))
                    {
                        var request = dependentRequests.Last.Value;
                        dependentRequests.RemoveLast();
                        _loadMaterialQueue.ConcurrentEnqueue(request).Wait();
                    }
                }
            }
            else
            {
                // Queue for retry
                _loadMaterialQueue.ConcurrentEnqueue(loadRequest).Wait();
            }
        }

        private IEnumerator SucceedGetGeometryBufferRequest(string modelPath, GeometryBuffer buffer)
        {
            // Check to see if any other requests were waiting on this model
            LinkedList<LoadCubeRequest> dependentRequests;
            while (!Monitor.TryEnter(_dependentCubes))
            {
                yield return null;
            }
            if (_dependentCubes.TryGetValue(modelPath, out dependentRequests))
            {
                _dependentCubes.Remove(modelPath);
            }
            Monitor.Exit(_dependentCubes);

            // If any were send them to their next stage
            if (dependentRequests != null)
            {
                foreach (var request in dependentRequests)
                {
                    request.GeometryBuffer = buffer;
                    MoveRequestForward(request);
                }
            }
        }

        // Called when the material data has been constructed into the cache
        // The material data is constructed using a materialkey for reference
        // The method sets the material data for any dependent requests and moves them along
        private IEnumerator SucceedGetMaterialDataRequests(string materialDataKey, MaterialData materialData)
        {
            CheckIfMainThread();
            // Check to see if any other requests were waiting on this model
            LinkedList<LoadCubeRequest> dependentRequests;
            while (!Monitor.TryEnter(_dependentCubes))
            {
                yield return null;
            }
            if (_dependentCubes.TryGetValue(materialDataKey, out dependentRequests))
            {
                _dependentCubes.Remove(materialDataKey);
            }
            Monitor.Exit(_dependentCubes);
            while (!Monitor.TryEnter(MaterialDataCache))
            {
                yield return null;
            }
            // If any were send them to their next stage
            if (dependentRequests != null)
            {
                foreach (var request in dependentRequests)
                {
                    request.MaterialData = materialData;

                    MaterialDataCache.AddRef(request.MaterialData.DiffuseTexPath);

                    MoveRequestForward(request);
                }
            }
            // Now that added references for the dependent requests. We can release the interim reference
            MaterialDataCache.Release(materialData.DiffuseTexPath);
            Monitor.Exit(MaterialDataCache);
        }

        // Determine the next appropriate queue for the request
        private void MoveRequestForward(LoadCubeRequest loadRequest)
        {
#if !UNITY_WSA
            var onMainThread = _mainThread.Equals(Thread.CurrentThread);
#else
    // Can't check for UIThread yet on Windows Store Apps so assume we are not (trying to StartCoroutine will fail otherwise)
            var onMainThread = false;
#endif

            if (loadRequest.GeometryBuffer == null)
            {
                if (onMainThread)
                {
                    StartCoroutine(_loadGeometryBufferQueue.ConcurrentEnqueue(loadRequest));
                }
                else
                {
                    _loadGeometryBufferQueue.ConcurrentEnqueue(loadRequest).Wait();
                }
            }
            else if (loadRequest.MaterialData == null)
            {
                if (onMainThread)
                {
                    StartCoroutine(_loadMaterialQueue.ConcurrentEnqueue(loadRequest));
                }
                else
                {
                    _loadMaterialQueue.ConcurrentEnqueue(loadRequest).Wait();
                }
            }
            else
            {
                if (onMainThread)
                {
                    StartCoroutine(_buildCubeRequests.ConcurrentEnqueue(loadRequest));
                }
                else
                {
                    _buildCubeRequests.ConcurrentEnqueue(loadRequest).Wait();
                }
            }
        }

        // Responsible for getting the geometry data for a given request
        // The method works roughly as follows
        // 1. Check if model data is in cache
        //    a. If not, start a web request for the data and add this request to the dependency list for the path
        // 2. If the model data cache indicates that it is being filled (a set value of null) (including if the request
        //      just started during this invocation) add the request to the dependency list for this path 
        // 3. If the model is in the cache and set then get the data for the request and move it forward
        private IEnumerator GetModelForRequest(LoadCubeRequest loadRequest)
        {
            var modelPath = loadRequest.Query.GetModelPath(loadRequest.LodIndex, loadRequest.X, loadRequest.Y,
                loadRequest.Z, ModelFormatString);
            while (!Monitor.TryEnter(_eboCache))
            {
                yield return null;
            }
            // If the geometry data is being loaded or this is the first request to load it add the request the dependency list
            if (!_eboCache.ContainsKey(modelPath) || _eboCache[modelPath] == null)
            {
                yield return StartCoroutine(AddDependentRequest(loadRequest, modelPath));

                if (!_eboCache.ContainsKey(modelPath))
                {
                    // Model data was not present in cache nor has any request started constructing it
                    EboCacheMisses++;

                    _eboCache[modelPath] = null;
                    Monitor.Exit(_eboCache);
                    if (UseWwwForEbo)
                    {
                        var cachePath = CacheWebRequest.GetCacheFilePath(modelPath);
                        WWW modelWww;
                        if (CacheWebRequest.IsItemInCache(cachePath))
                        {
                            FileCacheHits++;
                            modelWww = new WWW("file:///" + cachePath);
                            yield return modelWww;
                        }
                        else
                        {
                            FileCacheMisses++;
                            modelWww = new WWW(modelPath);
                            yield return modelWww;
                            if (modelWww.Succeeded())
                            {
                                CacheWebRequest.AddToCache(cachePath, modelWww.bytes);
                            }
                        }

                        if (modelWww.Failed())
                        {
                            Debug.LogError("Error getting model [" + modelPath + "] " +
                                           modelWww.error);
                            FailGetGeometryBufferRequest(loadRequest, modelPath);
                        }
                        else
                        {
                            var buffer =
                                new GeometryBuffer(_geometryBufferAltitudeTransform, true)
                                {
                                    Buffer = modelWww.bytes,
                                    Format = ModelFormat
                                };
                            BetterThreadPool.QueueUserWorkItem(s =>
                            {
                                lock (_eboCache)
                                {
                                    _eboCache[modelPath] = buffer;
                                }
                                buffer.Process();
                                SucceedGetGeometryBufferRequest(modelPath, buffer).Wait();
                            });
                        }
                    }
                    else
                    {
                        CacheWebRequest.GetBytes(modelPath, modelResponse =>
                        {
                            lock (_eboCache)
                            {
                                if (modelResponse.Status == CacheWebRequest.CacheWebResponseStatus.Error)
                                {
                                    Debug.LogError("Error getting model [" + modelPath + "] " +
                                                   modelResponse.ErrorMessage);
                                    FailGetGeometryBufferRequest(loadRequest, modelPath);
                                }
                                else if (modelResponse.Status == CacheWebRequest.CacheWebResponseStatus.Cancelled)
                                {
                                    _eboCache.Remove(modelPath);
                                }
                                else
                                {
                                    if (modelResponse.IsCacheHit)
                                    {
                                        FileCacheHits++;
                                    }
                                    else
                                    {
                                        FileCacheMisses++;
                                    }
                                    var buffer = new GeometryBuffer(_geometryBufferAltitudeTransform, true)
                                    {
                                        Buffer = modelResponse.Content,
                                        Format = ModelFormat
                                    };
                                    _eboCache[modelPath] = buffer;
                                    buffer.Process();
                                    SucceedGetGeometryBufferRequest(modelPath, buffer).Wait();
                                }
                            }
                        }, DependentRequestsExistBlocking);
                    }
                }
                else
                {
                    Monitor.Exit(_eboCache);
                }
            }
            else // The model data was in the cache
            {
                // Model was constructed move request to next step
                EboCacheHits++;
                loadRequest.GeometryBuffer = _eboCache[modelPath];
                MoveRequestForward(loadRequest);
                Monitor.Exit(_eboCache);
            }
        }

        // Responsible for getting the material data for a load request
        // The method works roughly as follows
        // 1. Check if material data is in cache
        //    a. If not, start a web request for the data and add this request to the dependency list for the path
        // 2. If the material data cache indicates that it is being filled (a set value of null) (including if the 
        //      request just started during this invocation) add the request to the dependency list for this path 
        // 3. If the material is in the cache and set then get the data for the request and move it forward
        private IEnumerator GetMaterialForRequest(LoadCubeRequest loadRequest)
        {
            var pyriteLevel =
                loadRequest.Query.DetailLevels[loadRequest.LodIndex];
            var textureCoordinates = pyriteLevel.TextureCoordinatesForCube(loadRequest.X, loadRequest.Y);
            var texturePath = loadRequest.Query.GetTexturePath(loadRequest.LodIndex,
                (int) textureCoordinates.x,
                (int) textureCoordinates.y);

            while (!Monitor.TryEnter(MaterialDataCache))
            {
                yield return null;
            }
            // If the material data is not in the cache or in the middle of being constructed add this request as a dependency
            if (!MaterialDataCache.ContainsKey(texturePath) || MaterialDataCache[texturePath] == null)
            {
                // Add this requst to list of requests that is waiting for the data
                yield return StartCoroutine(AddDependentRequest(loadRequest, texturePath));

                // Check if this is the first request for material (or it isn't in the cache)
                if (!MaterialDataCache.ContainsKey(texturePath))
                {
                    if (UseWwwForTextures)
                    {
                        // Material data was not in cache nor being constructed 
                        // Cache counter
                        MaterialCacheMisses++;
                        // Set to null to signal to other tasks that the key is in the process
                        // of being filled
                        MaterialDataCache[texturePath] = null;
                        var materialData = CubeBuilderHelpers.GetDefaultMaterialData((int) textureCoordinates.x,
                            (int) textureCoordinates.y, loadRequest.LodIndex,
                            texturePath);
                        var cachePath = CacheWebRequest.GetCacheFilePath(texturePath);
                        if (!CacheFill)
                        {
                            WWW textureWww; // = new WWW(texturePath);
                            if (CacheWebRequest.IsItemInCache(cachePath))
                            {
                                FileCacheHits++;
                                textureWww = new WWW("file:///" + cachePath);
                                yield return textureWww;
                            }
                            else
                            {
                                FileCacheMisses++;
                                textureWww = new WWW(texturePath);
                                yield return textureWww;
                                if (textureWww.Succeeded())
                                {
                                    CacheWebRequest.AddToCache(cachePath, textureWww.bytes);
                                }
                            }
                            if (textureWww.Failed())
                            {
                                Debug.LogError("Error getting texture [" + texturePath + "] " +
                                               textureWww.error);
                                FailGetMaterialDataRequest(loadRequest, texturePath);
                            }
                            else
                            {
                                materialData.DiffuseTex = textureWww.textureNonReadable;
                                materialData.DiffuseTex.name = materialData.Name;
                            }
                        }
                        MaterialDataCache[texturePath] = materialData;
                        // Add a reference to keep the texture around until we queue off the related load requests
                        MaterialDataCache.AddRef(texturePath);
                        // Move forward dependent requests that wanted this material data
                        yield return StartCoroutine(SucceedGetMaterialDataRequests(texturePath, materialData));
                    }
                    else
                    {
                        // Material data was not in cache nor being constructed 
                        // Cache counter
                        MaterialCacheMisses++;
                        // Set to null to signal to other tasks that the key is in the process
                        // of being filled
                        MaterialDataCache[texturePath] = null;
                        var materialData = CubeBuilderHelpers.GetDefaultMaterialData((int) textureCoordinates.x,
                            (int) textureCoordinates.y, loadRequest.LodIndex,
                            texturePath);
                        _partiallyConstructedMaterialDatas[texturePath] = materialData;

                        CacheWebRequest.GetBytes(texturePath, textureResponse =>
                        {
                            CheckIfBackgroundThread();
                            if (textureResponse.Status == CacheWebRequest.CacheWebResponseStatus.Error)
                            {
                                Debug.LogError("Error getting texture [" + texturePath + "] " +
                                               textureResponse.ErrorMessage);
                                FailGetMaterialDataRequest(loadRequest, texturePath);
                            }
                            else if (textureResponse.Status == CacheWebRequest.CacheWebResponseStatus.Cancelled)
                            {
                                lock (MaterialDataCache)
                                {
                                    MaterialDataCache.Remove(texturePath);
                                }
                            }
                            else
                            {
                                if (textureResponse.IsCacheHit)
                                {
                                    FileCacheHits++;
                                }
                                else
                                {
                                    FileCacheMisses++;
                                }
                                _texturesReadyForMaterialDataConstruction.ConcurrentEnqueue(
                                    new KeyValuePair<string, byte[]>(texturePath, textureResponse.Content)).Wait();
                            }
                        }, DependentRequestsExistBlocking);
                    }
                }
            }
            else // The material was in the cache
            {
                // Material data ready get it and move on
                MaterialCacheHits++;
                loadRequest.MaterialData = MaterialDataCache[texturePath];
                MaterialDataCache.AddRef(texturePath);
                MoveRequestForward(loadRequest);
            }
            Monitor.Exit(MaterialDataCache);
        }

        // Used to create material data when a texture has finished downloading
        private IEnumerator FinishCreatingMaterialDataWithTexture(
            KeyValuePair<string, byte[]> materialDataKeyAndTexturePair)
        {
            var materialDataKey = materialDataKeyAndTexturePair.Key;
            while (!Monitor.TryEnter(_partiallyConstructedMaterialDatas))
            {
                yield return null;
            }
            var inProgressMaterialData = _partiallyConstructedMaterialDatas[materialDataKey];
            _partiallyConstructedMaterialDatas.Remove(materialDataKey);
            Monitor.Exit(_partiallyConstructedMaterialDatas);
            if (!CacheFill)
            {
#if UNITY_IOS
                var texture = new Texture2D(1, 1, TextureFormat.RGB24, false);
#else
                var texture = new Texture2D(1, 1, TextureFormat.DXT1, false);
#endif
                texture.LoadImage(materialDataKeyAndTexturePair.Value);

                inProgressMaterialData.DiffuseTex = texture;
                inProgressMaterialData.DiffuseTex.name = inProgressMaterialData.Name;
            }
            while (!Monitor.TryEnter(MaterialDataCache))
            {
                yield return null;
            }
            MaterialDataCache[materialDataKey] = inProgressMaterialData;
            // Add reference until we add references for dependent requests
            MaterialDataCache.AddRef(materialDataKey);
            Monitor.Exit(MaterialDataCache);
            // Move forward dependent requests that wanted this material data
            yield return StartCoroutine(SucceedGetMaterialDataRequests(materialDataKey, inProgressMaterialData));
        }

        // Used to create a and populate a game object for this request 
        private IEnumerator BuildCubeRequest(LoadCubeRequest loadRequest)
        {
            Build(loadRequest.GeometryBuffer, loadRequest.MaterialData, loadRequest.X, loadRequest.Y, loadRequest.Z,
                loadRequest.Query.DetailLevels[loadRequest.LodIndex].Value, loadRequest.RegisterCreatedObjects);
            yield break;
        }

        private void Build(GeometryBuffer buffer, MaterialData materialData, int x, int y, int z, int lod,
            Action<GameObject> registerCreatedObjects)
        {
            if (CacheFill)
            {
                return;
            }

            var cubeName = new StringBuilder("cube_L");
            cubeName.Append(lod);
            cubeName.Append(':');
            cubeName.Append(x);
            cubeName.Append('_');
            cubeName.Append(y);
            cubeName.Append('_');
            cubeName.Append(z);

            var newCube = ObjectPooler.Current.GetPooledObject(BaseModelCube);
            newCube.name = cubeName.ToString();
            buffer.PopulateMeshes(newCube, materialData.Material);
            // Put object in scene, claim from pool
            newCube.SetActive(true);

            if (registerCreatedObjects != null)
            {
                registerCreatedObjects(newCube);
            }
        }
    }
}