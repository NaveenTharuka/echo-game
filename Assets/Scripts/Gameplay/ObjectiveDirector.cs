using System.Collections;
using Echo.AI;
using Echo.Interaction;
using Echo.Navigation;
using Echo.Player;
using Echo.Sound;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Echo.Gameplay
{
    /// The level's goal (Phase 2): get the key card (access pass, Key room) and restore power (fusebox in the warehouse) to open
    /// the Lab door, take the medicine, then get back out to where you came in.
    ///   - The fusebox starts a generator: a loud sound right in the Tracker's territory - you choose when to make it.
    ///   - The Lab door opens only with the pass AND power; trying it says what is missing.
    ///   - Taking the medicine trips an alarm: a siren at the Lab that every agent can hear; each wail tells them
    ///     "something is at the Lab" (a call from there, never the player's position). The way out is the way in,
    ///     so the escape runs into the hunters coming the other way.
    ///   - Reaching the exit with the medicine wins.
    /// Wired in SampleScene by ECHO > SampleScene > 7. Objectives. Shows a one-line objective at the top left.
    public class ObjectiveDirector : MonoBehaviour
    {
        public enum Stage { FindWay, OpenLab, TakeMedicine, Escape, Won }

        [Header("Scene")]
        public Door labDoor;
        public SwitchInteractable powerSwitch;
        public SwitchInteractable accessPass;
        public SwitchInteractable medicine;
        public Transform exit;
        public float exitRadius = 3f;

        [Header("Generator (fusebox)")]
        [Tooltip("Start-up roar the AI hears (intensity x 45 m x sensitivity).")]
        public float generatorIntensity = 0.75f;

        [Header("Alarm (medicine)")]
        public float alarmSeconds = 75f;
        public float wailInterval = 6f;
        [Tooltip("Pack-call strength of each wail: big enough to reach the whole facility through its corridors.")]
        public float alarmCallIntensity = 3f;
        [Tooltip("How urgent a wail sounds to an agent (0..1): high enough that hunters run, not stroll.")]
        public float alarmLoudness = 0.9f;
        public float alarmError = 6f;
        [Tooltip("The siren at the Lab (3D: muffled by walls, distant down the corridors).")]
        public float sirenVolume = 0.9f;
        [Tooltip("The same alarm over the facility's PA, everywhere (2D), so it is never lost entirely.")]
        public float paVolume = 0.22f;

        public Stage Current { get; private set; } = Stage.FindWay;
        public bool HasPass { get; private set; }
        public bool PowerOn { get; private set; }
        public bool HasMedicine { get; private set; }
        public float ElapsedSeconds => Time.time - startTime;

        Text objectiveLabel, bigLabel;
        CanvasGroup bigGroup;
        AudioSource siren, sirenLab;
        AudioOcclusion sirenLabOcclusion;
        float sirenFade = 1f;
        float startTime, alarmUntil, alarmStart, nextWail;
        Transform player;

        void Start()
        {
            startTime = Time.time;
            var fpc = FindFirstObjectByType<StarterAssets.FirstPersonController>();
            if (fpc != null) player = fpc.transform;
            var interactor = FindFirstObjectByType<PlayerInteractor>();
            if (interactor != null) interactor.Interacted += OnInteracted;

            if (powerSwitch != null) powerSwitch.onActivated.AddListener(OnPower);
            if (accessPass != null) accessPass.onActivated.AddListener(OnPass);
            if (medicine != null) medicine.onActivated.AddListener(OnMedicine);
            if (labDoor != null && !labDoor.isLocked) labDoor.Lock();   // pass + power, nothing less

            BuildHud();
            Refresh();
        }

        // ------------------------------------------------------------------ steps

        void OnPower()
        {
            if (PowerOn) return;
            PowerOn = true;
            // The generator coughs into life: loud, mechanical, right where the Tracker hunts.
            Vector3 p = powerSwitch.transform.position;
            Noise.Make(SoundType.Generator, p, generatorIntensity, SoundSource.Player, powerSwitch.gameObject);
            Noise.Make(SoundType.MetalImpact, p, generatorIntensity * 0.7f, SoundSource.Player, powerSwitch.gameObject);
            HudMessage.Show("Power restored. That was loud.", 3f);
            TryOpenLab();
        }

        void OnPass()
        {
            HasPass = true;
            TryOpenLab();
        }

        void TryOpenLab()
        {
            if (HasPass && PowerOn && labDoor != null && labDoor.isLocked)
            {
                labDoor.Unlock();
                HudMessage.Show("Lab door unlocked", 2.5f);
            }
            Refresh();
        }

        void OnInteracted(IInteractable what)
        {
            if (labDoor == null || !ReferenceEquals(what, labDoor) || !labDoor.isLocked) return;
            string need = !PowerOn && !HasPass ? "No power. And it needs the key card."
                        : !PowerOn ? "No power. The fusebox is somewhere in the warehouse."
                        : "Needs the key card from the Key room.";
            HudMessage.Show("Locked. " + need, 3f);
        }

        void OnMedicine()
        {
            if (HasMedicine) return;
            HasMedicine = true;
            alarmUntil = Time.time + alarmSeconds; alarmStart = Time.time;
            nextWail = Time.time;
            StartSiren();
            HudMessage.Show("ALARM. Get out. Now.", 4f);
            Refresh();
        }

        void Update()
        {
            if (Current == Stage.Won) return;
            if (HasMedicine && Time.time < alarmUntil && Time.time >= nextWail)
            {
                nextWail = Time.time + wailInterval;
                Wail();
            }
            if (siren != null && siren.isPlaying && Time.time >= alarmUntil)
            {
                sirenFade = Mathf.MoveTowards(sirenFade, 0f, Time.deltaTime * 0.2f);
                siren.volume = paVolume * sirenFade;
                if (sirenLabOcclusion != null) sirenLabOcclusion.SetBaseVolume(sirenVolume * sirenFade);
            }

            if (HasMedicine && player != null && exit != null &&
                Vector3.Distance(player.position, exit.position) <= exitRadius)
                Win();
        }

        /// One wail: every agent that can hear the siren (through the facility's corridors) gets "something at the
        /// Lab" - the siren's place, not the player's.
        void Wail()
        {
            Vector3 at = medicine != null ? medicine.transform.position : labDoor.DoorwayCenter;
            foreach (var a in AgentRegistry.All)
            {
                if (a == null || !a.IsAlive) continue;
                if (a.TryGetComponent(out Hearing ear) && ear.HearCall(at, alarmCallIntensity, at, alarmError, perceivedLoudness: alarmLoudness)
                    && Time.time - alarmStart < 0.5f)
                    a.OnAlarm(at);   // the first wail: agents with a plan for an alarm make it now
            }
        }

        void Win()
        {
            Current = Stage.Won;
            if (siren != null) siren.Stop();
            if (sirenLab != null) sirenLab.Stop();
            if (player != null)
                foreach (var b in player.GetComponents<Behaviour>())
                    if (b is StarterAssets.FirstPersonController || b is PlayerInteractor || b is PlayerDeath) b.enabled = false;
            int s = Mathf.FloorToInt(ElapsedSeconds);
            StartCoroutine(ShowEnd($"YOU GOT OUT\n<size=22>with the medicine - {s / 60}:{s % 60:00}</size>"));
            Debug.Log($"[Objective] escaped with the medicine after {ElapsedSeconds:0} s", this);
        }

        IEnumerator ShowEnd(string text)
        {
            bigLabel.text = text;
            for (float t = 0f; t < 1.5f; t += Time.unscaledDeltaTime) { bigGroup.alpha = t / 1.5f; yield return null; }
            bigGroup.alpha = 1f;
            yield return new WaitForSecondsRealtime(6f);
            Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            SceneManager.LoadScene(scene.buildIndex);
#endif
        }

        // ------------------------------------------------------------------ HUD

        void Refresh()
        {
            Current = HasMedicine ? Stage.Escape
                    : HasPass && PowerOn ? Stage.TakeMedicine
                    : HasPass || PowerOn ? Stage.OpenLab
                    : Stage.FindWay;
            if (objectiveLabel == null) return;
            objectiveLabel.text = Current switch
            {
                Stage.FindWay => "Find the medicine in the locked Lab",
                Stage.OpenLab => !PowerOn ? "Restore power to the Lab door - fusebox, warehouse"
                                          : "Find the key card - Key room, below the Lab stairs",
                Stage.TakeMedicine => "Take the medicine from the Lab",
                Stage.Escape => "Get out - back the way you came in",
                _ => ""
            };
        }

        void BuildHud()
        {
            var canvasGo = new GameObject("ObjectiveHUD (runtime)", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 900;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            objectiveLabel = Label(canvasGo.transform, "Objective", font, 20, TextAnchor.UpperLeft,
                                   new Vector2(0f, 1f), new Vector2(40f, -36f), new Vector2(900f, 40f));
            objectiveLabel.color = new Color(0.85f, 0.85f, 0.8f, 0.75f);

            var end = new GameObject("End", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            end.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)end.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            end.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.9f);
            bigGroup = end.GetComponent<CanvasGroup>(); bigGroup.alpha = 0f; bigGroup.blocksRaycasts = false;
            bigLabel = Label(end.transform, "Text", font, 54, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 300f));
            bigLabel.supportRichText = true;
        }

        static Text Label(Transform parent, string name, Font font, int size, TextAnchor align, Vector2 anchor, Vector2 pos, Vector2 box)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Outline));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos; rt.sizeDelta = box;
            var t = go.GetComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = align; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            go.GetComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            return t;
        }

        // ------------------------------------------------------------------ siren

        /// The recorded siren winds up once, then loops (SoundLibrary sets Alarm_Siren_Start / Alarm_Siren_Loop);
        /// synthesised two-tone if the library has none. Two sources: the siren itself at the Lab (3D) and the PA (2D).
        void StartSiren()
        {
            var loopClips = SoundLibrary.ActiveSet("Alarm_Siren_Loop");
            var startClips = SoundLibrary.ActiveSet("Alarm_Siren_Start");
            var loop = loopClips.Length > 0 ? loopClips[0] : SirenClip();
            var start = startClips.Length > 0 ? startClips[0] : null;
            if (siren == null)
            {
                siren = gameObject.AddComponent<AudioSource>();
                siren.spatialBlend = 0f; siren.reverbZoneMix = 0f;   // the PA: everywhere in the facility
                var go = new GameObject("Siren (Lab)");
                go.transform.position = (medicine != null ? medicine.transform.position : labDoor.DoorwayCenter) + Vector3.up * 2f;
                sirenLab = go.AddComponent<AudioSource>();
                sirenLab.spatialBlend = 1f; sirenLab.dopplerLevel = 0f;
                sirenLab.rolloffMode = AudioRolloffMode.Logarithmic; sirenLab.minDistance = 8f; sirenLab.maxDistance = 140f;
                go.AddComponent<AudioLowPassFilter>();
                sirenLabOcclusion = go.AddComponent<AudioOcclusion>();
                sirenLabOcclusion.farDistance = 90f;                 // a siren cuts through: keep it brighter, longer
            }
            sirenFade = 1f;
            siren.volume = paVolume;
            sirenLabOcclusion.SetBaseVolume(sirenVolume);
            double t0 = AudioSettings.dspTime + 0.05;
            foreach (var s in new[] { siren, sirenLab })
            {
                s.loop = true; s.clip = loop;
                if (start != null)
                {
                    s.PlayOneShot(start);                              // wind-up, then the loop takes over
                    s.PlayScheduled(t0 + start.length - 0.35);
                }
                else s.Play();
            }
            sirenLabOcclusion.Snap();
        }

        /// Two-tone wail with a hard, distorted edge (fallback when the library has no siren recording).
        static AudioClip SirenClip()
        {
            const int rate = 44100; const float seconds = 3f;
            int n = (int)(rate * seconds);
            var data = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float sweep = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * t / seconds);   // up and down once per loop
                float f = Mathf.Lerp(520f, 980f, sweep);
                phase += 2.0 * Mathf.PI * f / rate;
                float s = Mathf.Sin((float)phase) + 0.35f * Mathf.Sin((float)(phase * 2.01)) + 0.15f * Mathf.Sin((float)(phase * 3.02));
                data[i] = (float)System.Math.Tanh(s * 1.6) * 0.6f;
            }
            var clip = AudioClip.Create("Siren (runtime)", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
