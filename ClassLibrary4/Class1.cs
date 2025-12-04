using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using System.Text.RegularExpressions;
using System.Reflection;

[assembly: MelonInfo(typeof(LobotomyVoiceMod.VoiceMod), "Lobotomy Corporation Dub Mod", "0.3.0", "Ayin")]
[assembly: MelonGame("Project_Moon", "Lobotomy")]

namespace LobotomyVoiceMod
{
    public class VoiceMod : MelonMod
    {
        public static VoiceMod Instance;
        
        private AudioSource _audioSource;
        public string _voiceFolder;
        private string _missingLogPath;
        
        private HashSet<string> _loggedMissingFiles = new HashSet<string>();
        public static Dictionary<object, string> CommandIdMap = new Dictionary<object, string>();
        public static string CurrentDayCache = "Common";

        // --- 核心设置：音量倍率 ---
        // 1.0 是原声。如果觉得小，可以改为 2.0f, 3.0f, 甚至 5.0f
        public static float GlobalVolumeMultiplier = 1.0f;

        public override void OnInitializeMelon()
        {
            Instance = this;
            _voiceFolder = Path.Combine(MelonEnvironment.UserLibsDirectory, "VoiceData");
            _missingLogPath = Path.Combine(_voiceFolder, "_MISSING_FILES.txt");
            
            if (!Directory.Exists(_voiceFolder)) Directory.CreateDirectory(_voiceFolder);
            
            try { File.AppendAllText(_missingLogPath, $"\n--- 游戏启动: {System.DateTime.Now} ---\n"); } catch {}
            LoggerInstance.Msg($"[初始化] 音量倍率已设定为: {GlobalVolumeMultiplier}x");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (_audioSource == null)
            {
                GameObject audioObj = new GameObject("VoiceMod_Player");
                GameObject.DontDestroyOnLoad(audioObj);
                
                _audioSource = audioObj.AddComponent<AudioSource>();
                
                // --- 关键修改 1: 强制 2D 声音 ---
                // spatialBlend = 0 (2D), 1 (3D)
                // 设为 0 意味着声音不会随距离衰减，永远是最大音量
                _audioSource.spatialBlend = 0f; 
                _audioSource.bypassListenerEffects = true; // 绕过场景里的混响效果
                _audioSource.playOnAwake = false;
                _audioSource.volume = 1.0f; // 这里的 1.0 已经是 Unity 的极限，剩下的靠数据修改
            }
        }

        // --- 关键修改 2: 音频数据硬扩音 ---
        // 直接修改内存中的波形数据
        private void AmplifyAudio(AudioClip clip, float multiplier)
        {
            if (multiplier <= 1.0f) return;

            float[] data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);

            for (int i = 0; i < data.Length; i++)
            {
                // 简单粗暴的乘法放大
                data[i] = data[i] * multiplier;
                
                // 防止爆音 (Clamp 在 -1 到 1 之间)
                if (data[i] > 1.0f) data[i] = 1.0f;
                else if (data[i] < -1.0f) data[i] = -1.0f;
            }

            clip.SetData(data, 0);
        }

        public void UpdateDayCacheFromId(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;
            string foundDay = null;

            var matchDay = Regex.Match(uniqueId, @"day\s*(\d+)", RegexOptions.IgnoreCase);
            if (matchDay.Success) foundDay = matchDay.Groups[1].Value;
            else 
            {
                var matchNum = Regex.Match(uniqueId, @"^(\d+)_");
                if (matchNum.Success) foundDay = matchNum.Groups[1].Value;
            }

            if (foundDay != null)
            {
                string newDayStr = $"Day{foundDay}";
                if (CurrentDayCache != newDayStr) CurrentDayCache = newDayStr;
            }
        }

        private void RecordMissingFile(string fileName, string originalText)
        {
            if (_loggedMissingFiles.Contains(fileName)) return;
            _loggedMissingFiles.Add(fileName);
            try { File.AppendAllText(_missingLogPath, $"{fileName} | 原文: {originalText}\n"); } catch {}
        }

        public void PlayVoiceByPath(string fullPath, string debugText)
        {
            string fileName = Path.GetFileName(fullPath);
            if (File.Exists(fullPath))
            {
                if (_audioSource != null) _audioSource.Stop();
                MelonCoroutines.Start(LoadAudioClip(fullPath));
                LoggerInstance.Msg($"[播放] {fileName}");
            }
            else
            {
                string shortText = debugText.Length > 15 ? debugText.Substring(0, 15) + "..." : debugText;
                LoggerInstance.Warning($"[缺失] {fileName}");
                RecordMissingFile(fileName, debugText);
            }
        }

        public void PlayVoiceFallback(string speaker, string text)
        {
            int textHash = GetStableHashCode(text);
            string fileName = $"{speaker}_{CurrentDayCache}_{textHash}.wav";
            string fullPath = Path.Combine(_voiceFolder, fileName);
            PlayVoiceByPath(fullPath, text);
        }

        IEnumerator LoadAudioClip(string path)
        {
            string url = "file://" + path;
            using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(url, UnityEngine.AudioType.WAV))
            {
                yield return uwr.SendWebRequest();
                if (!uwr.isNetworkError && !uwr.isHttpError && _audioSource != null)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                    
                    if (clip != null)
                    {
                        // --- 在播放前执行扩音 ---
                        AmplifyAudio(clip, GlobalVolumeMultiplier);
                        
                        _audioSource.clip = clip;
                        _audioSource.Play();
                    }
                }
            }
        }

        private int GetStableHashCode(string str)
        {
            int hash = 5381;
            for (int i = 0; i < str.Length; i++)
            {
                hash = ((hash << 5) + hash) + str[i];
            }
            return hash;
        }
    }

    [HarmonyPatch(typeof(StoryStaticDataModel), "GetSceneData")]
    public class StoryDataPatch
    {
        [HarmonyPostfix]
        public static void Postfix(string id, StoryScriptScene __result)
        {
            if (__result != null && __result.cmd != null && __result.cmd.list != null)
            {
                int speakIndex = 0;
                foreach (var cmd in __result.cmd.list)
                {
                    if (cmd is StoryScriptCommand_speak)
                    {
                        if (!VoiceMod.CommandIdMap.ContainsKey(cmd))
                        {
                            string uniqueId = $"{id}_{speakIndex}";
                            VoiceMod.CommandIdMap[cmd] = uniqueId;
                        }
                        speakIndex++;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(StoryUI), "Command_speak")]
    public class StoryPatch
    {
        private static readonly AccessTools.FieldRef<StoryUI, StoryScriptCommandData> CurrentCommandRef = 
            AccessTools.FieldRefAccess<StoryUI, StoryScriptCommandData>("_curCmd");

        private static FieldInfo _skipFieldInfo;
        private static bool _hasCheckedForField = false;

        [HarmonyPostfix]
        public static void Postfix(StoryUI __instance, StoryUI.StoryScriptCommandEventEnum e)
        {
            if (e != StoryUI.StoryScriptCommandEventEnum.EXECUTE) return;
            if (IsSkipping(__instance)) return;

            var baseCmd = CurrentCommandRef(__instance);
            var speakCmd = baseCmd as StoryScriptCommand_speak;

            if (speakCmd != null)
            {
                if (VoiceMod.CommandIdMap.TryGetValue(speakCmd, out string uniqueId))
                {
                    VoiceMod.Instance.UpdateDayCacheFromId(uniqueId);
                    string fileName = $"{speakCmd.speaker}_{uniqueId}.wav";
                    string fullPath = Path.Combine(VoiceMod.Instance._voiceFolder, fileName);
                    VoiceMod.Instance.PlayVoiceByPath(fullPath, speakCmd.text);
                }
                else
                {
                    VoiceMod.Instance.PlayVoiceFallback(speakCmd.speaker, speakCmd.text);
                }
            }
        }

        private static bool IsSkipping(StoryUI ui)
        {
            if (!_hasCheckedForField)
            {
                _hasCheckedForField = true;
                string[] candidates = { "_bSkip", "_isSkip", "_skipping", "skipping", "_skip", "bSkip", "_bAuto", "_isAuto" };
                foreach (var name in candidates)
                {
                    var f = AccessTools.Field(typeof(StoryUI), name);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        _skipFieldInfo = f;
                        break;
                    }
                }
            }
            if (_skipFieldInfo != null)
            {
                try { return (bool)_skipFieldInfo.GetValue(ui); } catch {}
            }
            return false;
        }
    }
}