﻿
using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using POpusCodec;
using POpusCodec.Enums;
using UnityEngine;
using Voice = ExitGames.Client.Photon.Voice;

// Wraps UnityEngine.AudioClip with Voice.IAudioStream interface.
// Used for playing back audio clips via Photon Voice.
internal class AudioClipWrapper : Voice.IAudioStream
{
    private AudioClip audioClip;
    private int readPos;
    private float startTime;
    public int SamplingRate { get { return audioClip.frequency; } }
    
    public bool Loop { get; set; }

    public AudioClipWrapper(AudioClip audioClip)
    {
        this.audioClip = audioClip;
        startTime = Time.time;
    }
    private bool playing = true;
    public bool GetData(float[] buffer)
    {
        if (!playing)
        {
            return false;
        }

        var playerPos = (int)((Time.time - startTime) * audioClip.frequency);
        var bufferSamplesCount = buffer.Length / audioClip.channels;
        if (playerPos > readPos + bufferSamplesCount)
        {
            this.audioClip.GetData(buffer, readPos);
            readPos += bufferSamplesCount;
            
            if (readPos >= audioClip.samples)
            {
                if (this.Loop)
                {
                    readPos = 0;
                    startTime = Time.time;
                }
                else
                {
                    playing = false;
                }
            }

            return true;
        }
        else
        {
            return false;
        }
    }
}

// Wraps UnityEngine.Microphone with Voice.IAudioStream interface.
internal class MicWrapper : Voice.IAudioStream
{
    private AudioClip mic;
    private string device;

    public MicWrapper(string device, int suggestedFrequency)
    {
        this.device = device;
        int minFreq;
        int maxFreq;
        Microphone.GetDeviceCaps(device, out minFreq, out maxFreq);
        var frequency = suggestedFrequency;
//        minFreq = maxFreq = 44100; // test like android client
        if (suggestedFrequency < minFreq || maxFreq != 0 && suggestedFrequency > maxFreq)
        {
            Debug.LogWarning("PUNVoice: MicWrapper does not support suggested frequency " + suggestedFrequency + " (min: " + minFreq + ",max: " + maxFreq + "). Setting to " + maxFreq);
            frequency = maxFreq;
        }
        this.mic = Microphone.Start(device, true, 1, frequency);
    }

    public int SamplingRate { get { return this.mic.frequency; } }
    public int Channels { get { return this.mic.channels; } }

    private int micPrevPos;
    private int micLoopCnt;
    private int readAbsPos;

    public bool GetData(float[] buffer)
    {
        int micPos = Microphone.GetPosition(this.device);
        // loop detection
        if (micPos < micPrevPos)
        {
            micLoopCnt++;            
        }
        micPrevPos = micPos;

        var micAbsPos = micLoopCnt * this.mic.samples + micPos;

        var bufferSamplesCount = buffer.Length / mic.channels;

        var nextReadPos = this.readAbsPos + bufferSamplesCount;
        if (nextReadPos < micAbsPos)
        {
            this.mic.GetData(buffer, this.readAbsPos % this.mic.samples);
            this.readAbsPos = nextReadPos;
            return true;
        }
        else
        {
            return false;
        }        
    }
}

/// <summary>
/// Component representing outgoing audio stream in scene. Should be attached to prefab with PhotonView attached.
/// </summary>
[RequireComponent(typeof(PhotonVoiceSpeaker))]
public class PhotonVoiceRecorder : Photon.MonoBehaviour
{
    private Voice.LocalVoice voice;
    
    private string microphoneDevice = null;
    private bool microphoneDeviceSet = false;
    private bool microphoneDeviceUsed = false;

    /// <summary>
    /// Set aidio clip in instector for playing back instead of microphone signal streaming.
    /// </summary>
    public AudioClip AudioClip;

    /// <summary>
    /// Loop playback for audio clip sources.
    /// </summary>
    public bool LoopAudioClip = true;

    /// <summary>
    /// Returns voice activity detector for recorder's audio stream.
    /// </summary>
    public ExitGames.Client.Photon.Voice.VoiceDetector VoiceDetector
    {
        get { return this.photonView.isMine ? this.voice.VoiceDetector : null; }
    }

    /// <summary>
    /// Set or get microphone device used for streaming. Set this in Awake(). 
    /// </summary>
    /// <remarks>
    /// Ignored and logs an error if set after PhotonVoiceRecorder.Start() called.
    /// </remarks>    
    public string MicrophoneDevice
    {
        get { return this.microphoneDevice; }
        set
        {
            if (this.microphoneDeviceUsed)
            {
                Debug.LogError("PUNVoice: PhotonVoiceRecorder's MicrophoneDevice already used. Can't set");
                return;
            }

            if (value != null && !Microphone.devices.Contains(value))
            {
                Debug.LogError("PUNVoice: " + value + " is not a valid microphone device");
                return;
            }

            this.microphoneDevice = value;
            this.microphoneDeviceSet = true;
        }
    }

    /// <summary>If AudioGroup != 0, recorders's audio data is sent only to clients listening to this group.</summary>
    /// <see PhotonVoiceNetwork.Client.ChangeAudioGroups/>
    public byte AudioGroup
    {
        get { return voice != null ? voice.AudioGroup : (byte)0; }
        set { if (voice != null) voice.AudioGroup = value; }
    }

    /// <summary>Returns true if audio stream broadcasts.</summary>
    public bool IsTransmitting
    {
        get { return voice != null && voice.IsTransmitting; }
    }

    /// <summary>
    /// Level meter utility.
    /// </summary>
    public ExitGames.Client.Photon.Voice.LevelMeter LevelMeter
    {
        get { return voice != null ? voice.LevelMeter : null; }
    }

    // give user a chance to change MicrophoneDevice in Awake()
    void Start()
    {
        if (photonView.isMine)
        {
            var pvs = PhotonVoiceSettings.Instance;

            if (!this.microphoneDeviceSet)
            {
                this.MicrophoneDevice = PhotonVoiceNetwork.MicrophoneDevice;
            }

            Application.RequestUserAuthorization(UserAuthorization.Microphone);
            // put required sample rate into audio source and encoder - both adjust it if needed
            Voice.IAudioStream audioStream;
            int channels = 0;
            if (AudioClip == null)
            {
                if (PhotonVoiceSettings.Instance.DebugInfo)
                {
                    Debug.Log("PUNVoice: Setting recorder's microphone device to " + this.MicrophoneDevice);
                }
                var mic = new MicWrapper(this.MicrophoneDevice, (int)pvs.SamplingRate);
                this.microphoneDeviceUsed = true;
                channels = mic.Channels;
                audioStream = mic;                
            }
            else
            {
                audioStream = new AudioClipWrapper(AudioClip);
                channels = AudioClip.channels;
                if (this.LoopAudioClip)
                {
                    ((AudioClipWrapper)audioStream).Loop = true;
                }
            }

            Voice.VoiceInfo voiceInfo = new Voice.VoiceInfo((int)pvs.SamplingRate, channels, (int)pvs.Delay, pvs.Bitrate, photonView.viewID);            
            this.voice = PhotonVoiceNetwork.CreateLocalVoice(audioStream, voiceInfo);

            this.VoiceDetector.On = PhotonVoiceSettings.Instance.VoiceDetection;
            this.VoiceDetector.Threshold = PhotonVoiceSettings.Instance.VoiceDetectionThreshold;
        }
    }

    void OnDestroy()
    {
        if (this.voice != null) // photonView.isMine does not work
        {
            PhotonVoiceNetwork.RemoveLocalVoice(this.voice);
        }
    }

    void OnEnable()
    {
        Application.RequestUserAuthorization(UserAuthorization.Microphone);
    }

    void OnDisable()
    {
    }

    // message sent by Voice client
    void OnJoinedVoiceRoom()
    {
        if (photonView.isMine)
        {
            this.voice.Transmit = PhotonVoiceSettings.Instance.AutoTransmit;
        }
    }

    /// <summary>If true, stream data broadcasted unconditionally.</summary>        
    public bool Transmit { get { return voice != null ? voice.Transmit : false; } set { if (voice != null) voice.Transmit = value; } }

    /// <summary>If true, voice detection enabled.</summary>
    public bool Detect { get { return voice != null ? voice.VoiceDetector.On : false; } set { if (voice != null) voice.VoiceDetector.On = value; } }

    /// <summary>Trigger voice detector calibration process.
    /// While calibrating, keep silence. Voice detector sets threshold basing on measured backgroud noise level.
    /// </summary>
    /// <param name="durationMs">Duration of calibration in milliseconds.</param>
    public void VoiceDetectorCalibrate(int durationMs)
    {
        if (photonView.isMine)
        {
            this.voice.VoiceDetectorCalibrate(durationMs);
        }
    }

    /// <summary>If true, voice detector calibration is in progress.</summary>
    public bool VoiceDetectorCalibrating { get { return this.voice.VoiceDetectorCalibrating; } }

    private string log0;
    private string log1;

    private string tostr<T>(T[] x, int lim = 10)
    {
        System.Text.StringBuilder b = new System.Text.StringBuilder();
        for (var i = 0; i < (x.Length < lim ? x.Length : lim); i++)
        {
            b.Append("-");
            b.Append(x[i]);
        }
        return b.ToString();
    }

    public string ToStringFull()
    {
        int minFreq = 0;
        int maxFreq = 0;
        Microphone.GetDeviceCaps(MicrophoneDevice, out minFreq, out maxFreq);
        return string.Format("Mic '{0}': {1}..{2} Hz", MicrophoneDevice, minFreq, maxFreq);
    }

}