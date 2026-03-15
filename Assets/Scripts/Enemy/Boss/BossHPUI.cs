using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Attach this to the BossHPUI root GameObject (the Canvas or panel).
/// 
/// Unity UI Hierarchy expected:
/// 
///   [Canvas] (Screen Space - Overlay)
///   └── BossHPUI          ← this script lives here
///       ├── BossNameText  ← TextMeshProUGUI
///       └── HPBarBG       ← Image (dark background strip)
///           ├── HPBarFill ← Image (red fill, Image Type: Filled, Fill Method: Horizontal)
///           └── HPBarGhost← Image (delayed ghost bar, slightly lighter red)
///
/// Assign references in the Inspector, then call BossHPUI.Instance.Show(bossName, maxHP)
/// from BossController when combat begins.
/// </summary>
public class BossHPUI : MonoBehaviour
{
    public static BossHPUI Instance { get; private set; }

    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI bossNameText;
    [SerializeField] private Image hpBarFill;
    [SerializeField] private Image hpBarGhost;   // optional ghost/delay bar
    [SerializeField] private CanvasGroup canvasGroup; // for fade in/out

    [Header("Settings")]
    [SerializeField] private float fadeInDuration  = 0.6f;
    [SerializeField] private float fadeOutDuration = 1.2f;
    [SerializeField] private float ghostDelay      = 0.8f;  // seconds before ghost drains
    [SerializeField] private float ghostDrainSpeed = 0.4f;  // how fast ghost drains

    private int   _maxHP;
    private float _currentFill;       // actual bar fill [0,1]
    private float _ghostFill;         // ghost bar fill  [0,1]
    private float _ghostTimer;        // countdown before ghost starts draining

    private Coroutine _fadeCoroutine;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Start fully hidden
        if (canvasGroup == null)
            canvasGroup = GetComponent<CanvasGroup>();

        canvasGroup.alpha          = 0f;
        canvasGroup.interactable   = false;
        canvasGroup.blocksRaycasts = false;
        gameObject.SetActive(false);
    }

    private void Update()
    {
        // Smoothly drain the ghost bar after a delay
        if (hpBarGhost == null) return;

        if (_ghostFill > _currentFill)
        {
            _ghostTimer -= Time.deltaTime;
            if (_ghostTimer <= 0f)
                _ghostFill = Mathf.MoveTowards(_ghostFill, _currentFill, ghostDrainSpeed * Time.deltaTime);
        }

        hpBarGhost.fillAmount = _ghostFill;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Called by BossController when the boss enters combat.</summary>
    public void Show(string bossName, int maxHP)
    {
        _maxHP        = maxHP;
        _currentFill  = 1f;
        _ghostFill    = 1f;
        _ghostTimer   = ghostDelay;

        bossNameText.text     = bossName;
        hpBarFill.fillAmount  = 1f;
        if (hpBarGhost != null) hpBarGhost.fillAmount = 1f;

        gameObject.SetActive(true);

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeTo(1f, fadeInDuration));
    }

    /// <summary>Called by BossController every time the boss takes damage.</summary>
    public void UpdateHP(int currentHP)
    {
        _currentFill = Mathf.Clamp01((float)currentHP / _maxHP);
        hpBarFill.fillAmount = _currentFill;

        // Reset ghost drain timer on each hit
        _ghostTimer = ghostDelay;
    }

    /// <summary>Called by BossController when the boss dies.</summary>
    public void Hide()
    {
        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeOutAndDisable());
    }

    // ── Coroutines ────────────────────────────────────────────────────────────

    private IEnumerator FadeTo(float targetAlpha, float duration)
    {
        float startAlpha = canvasGroup.alpha;
        float elapsed    = 0f;

        while (elapsed < duration)
        {
            elapsed          += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
            yield return null;
        }

        canvasGroup.alpha = targetAlpha;
    }

    private IEnumerator FadeOutAndDisable()
    {
        yield return StartCoroutine(FadeTo(0f, fadeOutDuration));
        gameObject.SetActive(false);
    }
}
