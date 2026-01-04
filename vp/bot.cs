using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using VpNet;
using RWXLoader;
using System.Reflection;

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

    [Header("Scale")]
    [Tooltip("How many VP world units equal 1 Unity unit. Set to 0.5 to render each VP unit as 2 Unity units.")]
    public float vpUnitsPerUnityUnit = 0.5f;

    [Header("Colliders")]
    [Tooltip("Add MeshColliders to loaded models so they are solid.")]
    public bool addModelColliders = true;

    [Header("Model Loader")]
    [Tooltip("Assign your RWXLoaderAdvanced here, or we'll create one at runtime")]
    public RWXLoaderAdvanced modelLoader;

    [Header("VP Object Server")]
    [Tooltip("Base URL of the VP build object server")]
    public string objectPath = "http://objects.virtualparadise.org/vpbuild/";
    [Tooltip("Password used for password-protected object paths")]
    public string objectPathPassword = "";

    [Header("VP Actions Debug")]
    public bool logCreateActions = true;
    public bool logActivateActions = true;

    private VirtualParadiseClient vpClient;
    private GameObject areaRoot;

    private struct PendingModelLoad
    {
        public string modelName;
        public UnityEngine.Vector3 position;
        public Quaternion rotation;
        public string action;
        public string description;
    }

    private readonly Queue<PendingModelLoad> pendingModelLoads = new Queue<PendingModelLoad>();
    private Coroutine loadQueueCoroutine;

    private void Start()
    {
        areaRoot = new GameObject($"VP_Area_{centerX}_{centerY}");
        StartCoroutine(InitializeAndLoad());
    }

    private IEnumerator InitializeAndLoad()
    {
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

        SetupModelLoader();
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

        await vpClient.LoginAndEnterAsync("", true);
        Debug.Log($"[VP] Connected & entered '{worldName}' as {userName}");
    }

    private void SetupModelLoader()
    {
        if (modelLoader == null)
        {
            var loaderGO = new GameObject("RWX Remote Loader");
            modelLoader = loaderGO.AddComponent<RWXLoaderAdvanced>();
        }

        modelLoader.defaultObjectPath = objectPath.TrimEnd('/') + "/";
        modelLoader.objectPathPassword = objectPathPassword;
        modelLoader.enableDebugLogs = true;
        modelLoader.parentTransform = areaRoot.transform;

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
                yield return null;
            }
        }

        while (pendingModelLoads.Count > 0 || loadQueueCoroutine != null)
            yield return null;
    }

    private IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
            yield return null;
    }

    private static string ExtractDescription(object obj)
    {
        if (obj == null) return string.Empty;

        var type = obj.GetType();
        var prop = type.GetProperty("Description", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (prop != null && prop.PropertyType == typeof(string))
            return prop.GetValue(obj) as string ?? string.Empty;

        var field = type.GetField("Description", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (field != null && field.FieldType == typeof(string))
            return field.GetValue(obj) as string ?? string.Empty;

        return string.Empty;
    }

    private void ProcessCell(QueryCellResult cell)
    {
        foreach (var obj in cell.Objects)
        {
            if (string.IsNullOrEmpty(obj.Model))
                continue;

            string modelName = obj.Model.EndsWith(".rwx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(obj.Model)
                : obj.Model;

            UnityEngine.Vector3 pos = VPtoUnity(obj.Position);
            Quaternion rot = ConvertVpRotationToUnity(obj.Rotation, obj.Angle, modelName);

            // ✅ action is per-instance metadata
            string action = obj.Action;
            string description = ExtractDescription(obj);

            EnqueueModelLoad(modelName, pos, rot, action, description);
        }
    }

    private void EnqueueModelLoad(string modelName, UnityEngine.Vector3 position, Quaternion rotation, string action, string description)
    {
        pendingModelLoads.Enqueue(new PendingModelLoad
        {
            modelName = modelName,
            position = position,
            rotation = rotation,
            action = action,
            description = description
        });

        if (loadQueueCoroutine == null)
            loadQueueCoroutine = StartCoroutine(ProcessLoadQueue());
    }

    private IEnumerator ProcessLoadQueue()
    {
        while (pendingModelLoads.Count > 0)
        {
            var request = pendingModelLoads.Dequeue();

            string modelId = System.IO.Path.GetFileNameWithoutExtension(request.modelName);

            bool completed = false;
            GameObject loadedObject = null;
            string errorMessage = null;
            List<VpActionCommand> createActions = null;
            List<VpActionCommand> activateActions = null;

            if (!string.IsNullOrWhiteSpace(request.action))
            {
                VP.VpActionParser.Parse(request.action, out createActions, out activateActions);
            }

            createActions ??= new List<VpActionCommand>();
            activateActions ??= new List<VpActionCommand>();

            bool activateOnInstantiate = createActions.Count == 0;

            modelLoader.LoadModelFromRemote(
                modelId,
                modelLoader.defaultObjectPath,
                (go, errMsg) =>
                {
                    loadedObject = go;
                    errorMessage = errMsg;
                    completed = true;
                },
                objectPathPassword,
                activateOnInstantiate
            );

            while (!completed)
                yield return null;

            if (loadedObject == null)
            {
                Debug.LogError($"RWX load failed: {request.modelName} (normalized='{modelId}') → {errorMessage}");
                yield return null;
                continue;
            }

            loadedObject.transform.localPosition = request.position;
            loadedObject.transform.localRotation = request.rotation;
            ApplyModelBaseScale(loadedObject);
            EnsureModelColliders(loadedObject);

            // Give the loader a frame to finish any late material/renderer setup
            yield return null;

            // ✅ Apply VP action semantics (create actions ONCE)
            if (createActions.Count > 0 || activateActions.Count > 0)
            {
                if (logCreateActions && createActions.Count > 0)
                    Debug.Log($"[VP Create] {loadedObject.name} will run {createActions.Count} actions");

                foreach (var a in createActions)
                    VpActionExecutor.ExecuteCreate(loadedObject, a, modelLoader.defaultObjectPath, objectPathPassword, this, request.description);

                if (activateActions.Count > 0)
                {
                    var act = loadedObject.GetComponent<VpActivateActions>();
                    if (act == null) act = loadedObject.AddComponent<VpActivateActions>();
                    act.actions.AddRange(activateActions);

                    if (logActivateActions)
                        Debug.Log($"[VP Activate] {loadedObject.name} stored {activateActions.Count} actions");
                }
            }

            if (!activateOnInstantiate && loadedObject != null && !loadedObject.activeSelf)
            {
                loadedObject.SetActive(true);
            }

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

    private float GetClampedVpUnitsPerUnityUnit()
    {
        return Mathf.Max(0.0001f, vpUnitsPerUnityUnit);
    }

    private float GetUnityUnitsPerVpUnit()
    {
        return 1f / GetClampedVpUnitsPerUnityUnit();
    }

    private UnityEngine.Vector3 GetBaseScaleVector()
    {
        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();
        return new UnityEngine.Vector3(unityUnitsPerVpUnit, unityUnitsPerVpUnit, unityUnitsPerVpUnit);
    }

    private void ApplyModelBaseScale(GameObject target)
    {
        if (target == null) return;

        UnityEngine.Vector3 baseScale = GetBaseScaleVector();
        var scaleContext = target.GetComponent<VpModelScaleContext>();
        if (scaleContext == null)
            scaleContext = target.AddComponent<VpModelScaleContext>();

        scaleContext.baseScale = baseScale;
        target.transform.localScale = baseScale;
    }

    private void EnsureModelColliders(GameObject target)
    {
        if (!addModelColliders || target == null)
            return;

        foreach (var filter in target.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter == null || filter.sharedMesh == null)
                continue;

            if (filter.GetComponent<Collider>() != null)
                continue;

            var collider = filter.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
        }
    }

    private UnityEngine.Vector3 VPtoUnity(VpNet.Vector3 vpPos)
    {
        float unityUnitsPerVpUnit = GetUnityUnitsPerVpUnit();
        return new UnityEngine.Vector3(
            -(float)vpPos.X * unityUnitsPerVpUnit,
            (float)vpPos.Y * unityUnitsPerVpUnit,
            (float)vpPos.Z * unityUnitsPerVpUnit
        );
    }
}
