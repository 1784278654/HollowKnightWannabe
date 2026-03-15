using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }

    [System.Serializable]
    public class SoundEntry
    {
        public string id;
        public AudioClip clip;

        [Range(0f, 1f)] public float volume = 1f;
        [Range(0.1f, 3f)] public float pitch = 1f;
        public bool loop = false;

        [Header("Optional")]
        public AudioMixerGroup mixerGroup;
    }

    [Header("Sound Library")]
    [SerializeField] private SoundEntry[] bgmSounds;
    [SerializeField] private SoundEntry[] sfxSounds;

    [Header("Audio Sources")]
    [SerializeField] private AudioSource bgmSource;
    [SerializeField] private AudioSource sfxSourcePrefab;
    [SerializeField] private int sfxPoolSize = 10;

    [Header("Persistent Settings")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    private Dictionary<string, SoundEntry> bgmDict = new();
    private Dictionary<string, SoundEntry> sfxDict = new();
    private List<AudioSource> sfxPool = new();

    private const string MASTER_VOL_KEY = "MasterVolume";
    private const string BGM_VOL_KEY = "BGMVolume";
    private const string SFX_VOL_KEY = "SFXVolume";

    private float masterVolume = 1f;
    private float bgmVolume = 1f;
    private float sfxVolume = 1f;

    public float MasterVolume => masterVolume;
    public float BGMVolume => bgmVolume;
    public float SFXVolume => sfxVolume;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (dontDestroyOnLoad)
            DontDestroyOnLoad(gameObject);

        InitializeDictionaries();
        InitializeAudioSources();
        LoadVolumeSettings();
        ApplyVolumes();
    }

    private void InitializeDictionaries()
    {
        bgmDict.Clear();
        sfxDict.Clear();

        foreach (SoundEntry sound in bgmSounds)
        {
            if (sound == null || string.IsNullOrWhiteSpace(sound.id) || sound.clip == null)
                continue;

            if (!bgmDict.ContainsKey(sound.id))
                bgmDict.Add(sound.id, sound);
            else
                Debug.LogWarning($"Duplicate BGM id found: {sound.id}", this);
        }

        foreach (SoundEntry sound in sfxSounds)
        {
            if (sound == null || string.IsNullOrWhiteSpace(sound.id) || sound.clip == null)
                continue;

            if (!sfxDict.ContainsKey(sound.id))
                sfxDict.Add(sound.id, sound);
            else
                Debug.LogWarning($"Duplicate SFX id found: {sound.id}", this);
        }
    }

    private void InitializeAudioSources()
    {
        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
            bgmSource.playOnAwake = false;
            bgmSource.loop = true;
        }

        if (sfxSourcePrefab == null)
        {
            GameObject temp = new GameObject("SFX_Source_Prefab");
            temp.transform.SetParent(transform);
            sfxSourcePrefab = temp.AddComponent<AudioSource>();
            sfxSourcePrefab.playOnAwake = false;
            sfxSourcePrefab.loop = false;
            sfxSourcePrefab.spatialBlend = 0f;
            temp.SetActive(false);
        }

        for (int i = 0; i < sfxPoolSize; i++)
        {
            AudioSource source = CreateSFXSource(i);
            sfxPool.Add(source);
        }
    }

    private AudioSource CreateSFXSource(int index)
    {
        GameObject obj = new GameObject($"SFX_Source_{index}");
        obj.transform.SetParent(transform);

        AudioSource source = obj.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = sfxSourcePrefab.spatialBlend;
        source.outputAudioMixerGroup = sfxSourcePrefab.outputAudioMixerGroup;

        return source;
    }

    private void LoadVolumeSettings()
    {
        masterVolume = PlayerPrefs.GetFloat(MASTER_VOL_KEY, 1f);
        bgmVolume = PlayerPrefs.GetFloat(BGM_VOL_KEY, 1f);
        sfxVolume = PlayerPrefs.GetFloat(SFX_VOL_KEY, 1f);
    }

    private void ApplyVolumes()
    {
        if (bgmSource != null)
            bgmSource.volume = masterVolume * bgmVolume;

        foreach (AudioSource source in sfxPool)
        {
            if (!source.isPlaying)
                source.volume = masterVolume * sfxVolume;
        }
    }

    private AudioSource GetAvailableSFXSource()
    {
        foreach (AudioSource source in sfxPool)
        {
            if (!source.isPlaying)
                return source;
        }

        AudioSource newSource = CreateSFXSource(sfxPool.Count);
        sfxPool.Add(newSource);
        return newSource;
    }

    public void PlayBGM(string id)
    {
        if (!bgmDict.TryGetValue(id, out SoundEntry sound))
        {
            Debug.LogWarning($"BGM not found: {id}", this);
            return;
        }

        if (bgmSource.clip == sound.clip && bgmSource.isPlaying)
            return;

        bgmSource.clip = sound.clip;
        bgmSource.pitch = sound.pitch;
        bgmSource.loop = sound.loop;
        bgmSource.volume = sound.volume * masterVolume * bgmVolume;
        bgmSource.outputAudioMixerGroup = sound.mixerGroup;
        bgmSource.Play();
    }

    public void StopBGM()
    {
        if (bgmSource.isPlaying)
            bgmSource.Stop();
    }

    public void PauseBGM()
    {
        if (bgmSource.isPlaying)
            bgmSource.Pause();
    }

    public void ResumeBGM()
    {
        if (!bgmSource.isPlaying && bgmSource.clip != null)
            bgmSource.UnPause();
    }

    public void PlaySFX(string id)
    {
        if (!sfxDict.TryGetValue(id, out SoundEntry sound))
        {
            Debug.LogWarning($"SFX not found: {id}", this);
            return;
        }

        AudioSource source = GetAvailableSFXSource();
        source.clip = sound.clip;
        source.pitch = sound.pitch;
        source.loop = sound.loop;
        source.volume = sound.volume * masterVolume * sfxVolume;
        source.outputAudioMixerGroup = sound.mixerGroup;
        source.Play();
    }

    public void PlaySFX(string id, float volumeMultiplier, float pitchMultiplier = 1f)
    {
        if (!sfxDict.TryGetValue(id, out SoundEntry sound))
        {
            Debug.LogWarning($"SFX not found: {id}", this);
            return;
        }

        AudioSource source = GetAvailableSFXSource();
        source.clip = sound.clip;
        source.pitch = sound.pitch * pitchMultiplier;
        source.loop = sound.loop;
        source.volume = sound.volume * volumeMultiplier * masterVolume * sfxVolume;
        source.outputAudioMixerGroup = sound.mixerGroup;
        source.Play();
    }

    public void StopSFX(string id)
    {
        if (!sfxDict.TryGetValue(id, out SoundEntry sound))
        {
            Debug.LogWarning($"SFX not found: {id}", this);
            return;
        }

        foreach (AudioSource audio in sfxPool)
        {
            if (audio.clip == sound.clip)
            {
                audio.Stop();
            }
        }
    }

    public void SetMasterVolume(float value)
    {
        masterVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(MASTER_VOL_KEY, masterVolume);
        PlayerPrefs.Save();
        ApplyVolumes();
    }

    public void SetBGMVolume(float value)
    {
        bgmVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(BGM_VOL_KEY, bgmVolume);
        PlayerPrefs.Save();
        ApplyVolumes();
    }

    public void SetSFXVolume(float value)
    {
        sfxVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(SFX_VOL_KEY, sfxVolume);
        PlayerPrefs.Save();
        ApplyVolumes();
    }

    public bool IsPlayingBGM(string id)
    {
        if (!bgmDict.TryGetValue(id, out SoundEntry sound))
            return false;

        return bgmSource.isPlaying && bgmSource.clip == sound.clip;
    }
}