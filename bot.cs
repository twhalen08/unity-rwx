using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using VpNet;
using RWXLoader;

public class VPWorldAreaLoader : MonoBehaviour
{

    [Header("VP Login")]
    public string userName = "Tom";
    public string botName = "Unity";
    public string applicationName = "Unity";
    public string worldName = "VP-Build";

    [Header("Area Settings")]
    public int centerX = 0;
    public int centerY = 0;
    public int radius = 1;

    [Header("Model Loader")]
    [Tooltip("Assign your RWXLoaderAdvanced here, or we'll create one at runtime")]
    public RWXLoaderAdvanced modelLoader;

    [Header("VP Object Server")]
    [Tooltip("Base URL of the VP build object server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";

    private VirtualParadiseClient vpClient;
    private GameObject areaRoot;

    private struct PendingModelLoad
    {
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
    }

    private readonly Queue<PendingModelLoad> pendingModelLoads = new Queue<PendingModelLoad>();
    private Coroutine loadQueueCoroutine;

    private void Start()
    {
        // 1) Create a root for this chunk of world
        areaRoot = new GameObject($"VP_Area_{centerX}_{centerY}");

        // Kick off the progressive initialization routine
        StartCoroutine(InitializeAndLoad());
    }

    private IEnumerator InitializeAndLoad()
    {
        // 2) Connect & enter the world
        var loginTask = ConnectAndLogin();
        yield return WaitForTask(loginTask);
        if (loginTask.IsFaulted || loginTask.IsCanceled)
        {
            string message = loginTask.IsFaulted
                ? loginTask.Exception?.GetBaseException().Message
                : "Login task was cancelled";
            Debug.LogError($"[VP] Failed to connect: {message}");
            yield break;
        }

        // 3) Ensure our loader and cache manager are set up
        SetupModelLoader();

        // 4) Query the cells & spawn all models progressively
        yield return QueryAndBuildAreaProgressively();
    }

    private async Task ConnectAndLogin()
    {
        vpClient = new VirtualParadiseClient
        {
            Configuration = new VirtualParadiseClientConfiguration
            {
                UserName = userName,
                BotName = botName,
                ApplicationName = applicationName,
                World = new World { Name = worldName }
            }
        };
        await vpClient.LoginAndEnterAsync("xxxxxxxx", true);
        Debug.Log($"[VP] Connected & entered '{worldName}' as {userName}");
    }

    private void SetupModelLoader()
    {
        // Guarantee we have an RWXLoaderAdvanced in the scene
        if (modelLoader == null)
        {
            var loaderGO = new GameObject("RWX Remote Loader");
            modelLoader = loaderGO.AddComponent<RWXLoaderAdvanced>();
        }

        // Point it at our VP object server, enable debug, and parent under areaRoot
        modelLoader.defaultObjectPath = objectPath.TrimEnd('/') + "/";
        modelLoader.enableDebugLogs = true;
        modelLoader.parentTransform = areaRoot.transform;

        // Also ensure the asset-manager singleton exists
        if (RWXAssetManager.Instance == null)
        {
            var mgrGO = new GameObject("RWX Asset Manager");
            mgrGO.AddComponent<RWXAssetManager>();
        }
    }

    private IEnumerator QueryAndBuildAreaProgressively()
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                var cellTask = vpClient.QueryCellAsync(centerX + dx, centerY + dy);
                yield return WaitForTask(cellTask);

                if (cellTask.IsFaulted || cellTask.IsCanceled)
                {
                    string message = cellTask.IsFaulted
                        ? cellTask.Exception?.GetBaseException().Message
                        : "Query was cancelled";
                    Debug.LogWarning($"[VP] Failed to query cell ({centerX + dx}, {centerY + dy}): {message}");
                    continue;
                }

                ProcessCell(cellTask.Result);
                // Give the queue processor a frame to start loading
                yield return null;
            }
        }

        // Wait for any outstanding model loads to complete before finishing
        while (pendingModelLoads.Count > 0 || loadQueueCoroutine != null)
        {
            yield return null;
        }
    }

    private IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
        {
            yield return null;
        }
    }

    private void ProcessCell(QueryCellResult cell)
    {
        foreach (var obj in cell.Objects)
        {
            if (string.IsNullOrEmpty(obj.Model))
                continue;

            // Strip trailing ".rwx" if present
            string modelName = obj.Model.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(obj.Model)
                : obj.Model;

            // Convert VP coords → Unity coords (swap X and Z)
            UnityEngine.Vector3 pos = VPtoUnity(obj.Position);

            Quaternion unityRot = ConvertVpRotationToUnity(obj.Rotation, obj.Angle, modelName);

            EnqueueModelLoad(modelName, pos, unityRot);
        }
    }

    private void EnqueueModelLoad(string modelName, UnityEngine.Vector3 position, Quaternion rotation)
    {
        pendingModelLoads.Enqueue(new PendingModelLoad
        {
            modelName = modelName,
            position = position,
            rotation = rotation
        });

        if (loadQueueCoroutine == null)
        {
            loadQueueCoroutine = StartCoroutine(ProcessLoadQueue());
        }
    }

    private IEnumerator ProcessLoadQueue()
    {
        while (pendingModelLoads.Count > 0)
        {
            var request = pendingModelLoads.Dequeue();

            bool completed = false;
            GameObject loadedObject = null;
            string errorMessage = null;

            modelLoader.LoadModelFromRemote(
                request.modelName,
                modelLoader.defaultObjectPath,
                (go, errMsg) =>
                {
                    loadedObject = go;
                    errorMessage = errMsg;
                    completed = true;
                });

            while (!completed)
            {
                yield return null;
            }

            if (loadedObject != null)
            {
                loadedObject.transform.localPosition = request.position;
                loadedObject.transform.localRotation = request.rotation;
                loadedObject.transform.localScale = UnityEngine.Vector3.one;
            }
            else
            {
                Debug.LogError($"RWX load failed: {request.modelName} → {errorMessage}");
            }

            // Allow other systems to breathe before the next heavy load
            yield return null;
        }

        loadQueueCoroutine = null;
    }

    private Quaternion ConvertVpRotationToUnity(VpNet.Vector3 vpRotation, double vpAngle, string modelName)
    {
        Quaternion unityRot = Quaternion.identity;
        UnityEngine.Vector3 rotationVector = new UnityEngine.Vector3((float)vpRotation.X, (float)vpRotation.Y, (float)vpRotation.Z);

        if (double.IsInfinity(vpAngle))
        {
            if (IsVectorValid(rotationVector))
            {
                // VP uses the rotation vector as Euler angles when the angle value is infinite
                // Flip Y/Z so the handedness matches Unity just like the previous implementation
                UnityEngine.Vector3 unityEuler = new UnityEngine.Vector3(rotationVector.x, -rotationVector.y, -rotationVector.z);
                unityRot = Quaternion.Euler(unityEuler);
            }
            else
            {
                Debug.LogWarning($"Invalid Euler rotation for {modelName}: {rotationVector}, using identity");
            }
        }
        else if (Math.Abs(vpAngle) > 0.001 && !double.IsNaN(vpAngle) && !double.IsInfinity(vpAngle))
        {
            if (!IsVectorValid(rotationVector))
            {
                Debug.LogWarning($"Invalid rotation axis for {modelName}: {rotationVector}, using identity");
            }
            else if (rotationVector.magnitude > 0.001f)
            {
                rotationVector = rotationVector.normalized;
                float angleInDegrees = (float)vpAngle * Mathf.Rad2Deg;

                if (!float.IsNaN(angleInDegrees) && !float.IsInfinity(angleInDegrees))
                {
                    Quaternion vpQuaternion = Quaternion.AngleAxis(angleInDegrees, rotationVector);
                    unityRot = ConvertVpQuaternionToUnity(vpQuaternion);
                }
                else
                {
                    Debug.LogWarning($"Invalid angle for {modelName}: {vpAngle} radians, using identity");
                }
            }
            else
            {
                Debug.LogWarning($"Zero rotation axis for {modelName}, using identity");
            }
        }

        if (!IsQuaternionValid(unityRot))
        {
            Debug.LogWarning($"Generated invalid quaternion for {modelName}, using identity");
            unityRot = Quaternion.identity;
        }

        return unityRot;
    }

    private Quaternion ConvertVpQuaternionToUnity(Quaternion vpQuaternion)
    {
        // Mirror the Virtual Paradise quaternion into Unity space by flipping Y/Z components
        Quaternion unityQuat = new Quaternion(vpQuaternion.x, -vpQuaternion.y, -vpQuaternion.z, vpQuaternion.w);
        return Quaternion.Normalize(unityQuat);
    }

    private static bool IsVectorValid(UnityEngine.Vector3 value)
    {
        return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) ||
                 float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
    }

    private static bool IsQuaternionValid(Quaternion value)
    {
        return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) || float.IsNaN(value.w) ||
                 float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z) || float.IsInfinity(value.w));
    }

    private UnityEngine.Vector3 VPtoUnity(VpNet.Vector3 vpPos)
    {
        // Convert VP coordinates to Unity with X-Y as ground plane and Z as height:
        // Flip only X axis to fix mirroring, keep Y and Z normal
        // VP.X (east/west on ground) → Unity.-X (flip east/west)
        // VP.Y (north/south on ground) → Unity.Y (normal north/south)
        // VP.Z (up/down height) → Unity.Z (up/down height)
        return new UnityEngine.Vector3(
            -(float)vpPos.X,  // VP ground X to Unity ground -X (flipped)
            (float)vpPos.Y,   // VP ground Y to Unity ground Y (normal)
            (float)vpPos.Z    // VP height Z to Unity height Z
        );
    }
}
