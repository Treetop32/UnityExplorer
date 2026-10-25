using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

#nullable enable

namespace UnityExplorer.ObjectExplorer;

public static class SceneHandler
{
    /// <summary>The currently inspected Scene.</summary>
    public static Scene? SelectedScene
    {
        get => selectedScene;
        internal set
        {
            if (selectedScene.HasValue && selectedScene == value)
            {
                return;
            }
            if (!value.HasValue)
            {
                return;
            }
            selectedScene = value;
            OnInspectedSceneChanged?.Invoke(selectedScene.Value);
        }
    }
    private static Scene? selectedScene;

    /// <summary>The GameObjects in the currently inspected scene.</summary>
    public static IEnumerable<GameObject> CurrentRootObjects { get; private set; } = new GameObject[0];

    /// <summary>All currently loaded Scenes.</summary>
    public static List<Scene> LoadedScenes { get; private set; } = new();

    /// <summary>The names of all scenes in the build settings, if they could be retrieved.</summary>
    public static List<string> AllSceneNames { get; private set; } = new();

    /// <summary>Invoked when the currently inspected Scene changes. The argument is the new scene.</summary>
    public static event Action<Scene>? OnInspectedSceneChanged;

    /// <summary>Invoked whenever the list of currently loaded Scenes changes. The argument contains all loaded scenes after the change.</summary>
    public static event Action<List<Scene>>? OnLoadedScenesUpdated;

    /// <summary>Generally will be 2, unless DontDestroyExists == false, then this will be 1.</summary>
    internal static int DefaultSceneCount => 1 + (DontDestroyExists ? 1 : 0);

    /// <summary>Whether or not we are currently inspecting the "HideAndDontSave" asset scene.</summary>
    // UNITY 6 FIX: Replaced .handle == -1 with !IsValid()
    public static bool InspectingAssetScene => SelectedScene.HasValue && !SelectedScene.Value.IsValid();

    /// <summary>Whether or not we successfuly retrieved the names of the scenes in the build settings.</summary>
    public static bool WasAbleToGetScenesInBuild { get; private set; }

    /// <summary>Whether or not the "DontDestroyOnLoad" scene exists in this game.</summary>
    public static bool DontDestroyExists { get; private set; }

    private const string dontDestroyName = "DontDestroyOnLoad";

    internal static void Init()
    {
        // Check if the game has "DontDestroyOnLoad"
        try
        {
            Type? sceneType = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.Scene");
            if (sceneType == null)
            {
                throw new Exception("This version of Unity does not ship with the 'Scene' class, or it was not unstripped.");
            }
            MethodInfo? method = sceneType.GetMethod("GetNameInternal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            
            // Note: This will throw in Unity 6 because the parameter type changed to SceneHandle.
            // That is expected and handled by the catch block below.
            string? sceneName = (string?)method?.Invoke(null, new object[] { -12 });
            if (string.IsNullOrEmpty(sceneName))
            {
                throw new Exception("Scene.GetNameInternal returned null for DontDestroyOnLoad scene.");
            }
            DontDestroyExists = sceneName == dontDestroyName;
        }
        catch (Exception ex)
        {
            ExplorerCore.LogWarning($"Unable to check for existence of DontDestroyOnLoad scene via Scene.GetNameInternal: {ex}");
#pragma warning disable CS0618 // Type or member is obsolete
            ExplorerCore.LogWarning("Falling back to checking loaded scenes for DontDestroyOnLoad via SceneManager.GetAllScenes().");
            DontDestroyExists = SceneManager.GetAllScenes().Any(s => s.name == dontDestroyName);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        // Try to get all scenes in the build settings. This may not work.
        try
        {
            Type sceneUtil = ReflectionUtility.GetTypeByName("UnityEngine.SceneManagement.SceneUtility");
            if (sceneUtil == null)
            {
                throw new Exception("This version of Unity does not ship with the 'SceneUtility' class, or it was not unstripped.");
            }

            MethodInfo? method = sceneUtil.GetMethod("GetScenePathByBuildIndex", ReflectionUtility.FLAGS);
            int sceneCount = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < sceneCount; i++)
            {
                string? scenePath = (string?)method?.Invoke(null, new object[] { i });
                if (string.IsNullOrEmpty(scenePath))
                {
                    continue;
                }
                AllSceneNames.Add(scenePath!);
            }

            WasAbleToGetScenesInBuild = true;
        }
        catch (Exception ex)
        {
            WasAbleToGetScenesInBuild = false;
            ExplorerCore.LogWarning($"Unable to generate list of all Scenes in the build: {ex}");
        }
    }

    internal static void Update()
    {
        // UNITY 6 FIX: Check by name and validity instead of hardcoded handle integers
        bool inspectedExists =
            SelectedScene.HasValue
            && ((DontDestroyExists && SelectedScene.Value.name == dontDestroyName)
                || !SelectedScene.Value.IsValid());

        LoadedScenes.Clear();

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded || !scene.IsValid())
            {
                continue;
            }

            // If we have not yet confirmed inspectedExists, check if this scene is our currently inspected one.
            if (!inspectedExists && SelectedScene.HasValue && scene == SelectedScene.Value)
            {
                inspectedExists = true;
            }

            LoadedScenes.Add(scene);
        }

        if (DontDestroyExists)
        {
            // UNITY 6 FIX: We can no longer reliably create a fake Scene with m_Handle = -12.
            // Instead, try to find the actual DDOL scene by name.
            var ddolScene = SceneManager.GetAllScenes().FirstOrDefault(s => s.name == dontDestroyName);
            if (ddolScene.IsValid() && !LoadedScenes.Contains(ddolScene))
            {
                LoadedScenes.Add(ddolScene);
            }
        }
        
        // UNITY 6 FIX: Add a placeholder for the "HideAndDontSave" asset scene.
        // default(Scene) is invalid, which perfectly matches our InspectingAssetScene check above.
        LoadedScenes.Add(default);

        // Default to first scene if none selected or previous selection no longer exists.
        if (!inspectedExists)
        {
            SelectedScene = LoadedScenes.Count > 0 ? LoadedScenes.First() : default;
        }

        // Notify on the list changing at all
        OnLoadedScenesUpdated?.Invoke(LoadedScenes);

        // Finally, update the root objects list.
        if (SelectedScene.HasValue && SelectedScene.Value.IsValid())
        {
            CurrentRootObjects = RuntimeHelper.GetRootGameObjects(SelectedScene.Value);
        }
        else
        {
            UnityEngine.Object[] allObjects = RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject));
            List<GameObject> objects = new();
            foreach (UnityEngine.Object obj in allObjects)
            {
                if (obj is GameObject go &&
                    go.transform != null &&
                    go.transform.parent == null && 
                    !go.scene.IsValid())
                {
                    objects.Add(go);
                }
            }
            CurrentRootObjects = objects;
        }
    }
}