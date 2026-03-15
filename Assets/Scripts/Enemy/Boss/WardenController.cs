using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WardenController — "The Warden of Ash"
///
/// A heavily armoured knight boss with 3 phases and 6 distinct attacks:
///
/// PHASE 1 (100% → 60% HP):
///   [1] GroundSlam   — close range, AOE shockwave forward
///   [2] ChargeThrust — mid range, linear dash with a stab at the end
///
/// PHASE 2 (60% → 30% HP):
///   [3] SpinSlash    — 360° spinning sweep, hits both sides
///   [4] LeapSlam     — jumps to player's position and craters down
///   [1][2] still available
///
/// PHASE 3 (30% → 0% HP):
///   [5] AshBarrage   — ranged: fires 3 homing ash projectiles in sequence
///   [6] FuryCombo    — close range: 3-hit melee combo, each hit faster
///   [1]–[4] still available, intervals reduced
///
/// Attack selection uses a weighted randomiser so the same attack never
/// fires twice in a row and phase-exclusive attacks are preferred when
/// their cooldowns are clear.
///
/// Animator trigger strings required:
///   "groundSlamStart", "groundSlamImpact"
///   "chargeThrustStart", "chargeThrustImpact"
///   "spinSlashStart", "spinSlashEnd"
///   "leapStart", "leapImpact"
///   "ashBarrageStart"
///   "furyComboStart", "furyComboHit2", "furyComboHit3"
///   "phaseTwo", "phaseThree"
///   "isDead", "Speed"  (float)
/// </summary>
public class WardenController : EnemyController
{
    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("Boss Info")]
    [SerializeField] private string bossDisplayName = "Warden of Ash";

    [Header("Movement")]
    public float walkSpeed       = 3.5f;
    public float chargeSpeed     = 14f;
    public float leapForce       = 22f;

    [Header("Ranges")]
    public float meleeRange      = 2.5f;   // close — GroundSlam, FuryCombo, SpinSlash
    public float chargeRange     = 8f;     // mid   — ChargeThrust, LeapSlam
    public float rangedRange     = 14f;    // far   — AshBarrage

    [Header("Damage")]
    public int groundSlamDamage  = 15;
    public int chargeDamage      = 20;
    public int spinDamage        = 18;
    public int leapDamage        = 25;
    public int ashProjectileDmg  = 12;
    public int furyHitDamage     = 10;

    [Header("Cooldowns (seconds)")]
    public float groundSlamCooldown  = 4f;
    public float chargeCooldown      = 6f;
    public float spinCooldown        = 7f;
    public float leapCooldown        = 8f;
    public float ashBarrageCooldown  = 9f;
    public float furyComboColdown    = 5f;

    [Header("Hitboxes")]
    [SerializeField] private Transform meleePoint;
    [SerializeField] private Transform spinCenter;
    [SerializeField] private Transform leapImpactPoint;
    [SerializeField] private Vector2   meleeBoxSize    = new Vector2(2.5f, 1.5f);
    [SerializeField] private float     spinRadius      = 2.8f;
    [SerializeField] private float     leapRadius      = 3f;
    [SerializeField] private LayerMask playerLayer;

    [Header("Projectile")]
    [SerializeField] private GameObject ashProjectilePrefab;
    [SerializeField] private Transform  projectileSpawnPoint;

    [Header("Phase thresholds (0–1)")]
    public float phaseTwoThreshold   = 0.6f;
    public float phaseThreeThreshold = 0.3f;

    public int maxHealth;

    // ── Private state ──────────────────────────────────────────────────────────

    private int   _phase = 1;
    private bool  _isActionLocked;
    private bool  _inCombat;

    // Per-attack cooldown timers (remaining seconds; 0 = ready)
    private float _cdGroundSlam;
    private float _cdCharge;
    private float _cdSpin;
    private float _cdLeap;
    private float _cdAshBarrage;
    private float _cdFuryCombo;

    private AttackType _lastAttack = AttackType.None;

    private Transform        _playerTransform;
    private Transform        _transform;
    private Rigidbody2D      _rigidbody;
    private Animator         _animator;
    private SpriteRenderer   _spriteRenderer;

    private enum AttackType
    {
        None,
        GroundSlam,
        ChargeThrust,
        SpinSlash,
        LeapSlam,
        AshBarrage,
        FuryCombo
    }

    // ── Unity lifecycle ────────────────────────────────────────────────────────

    private void Start()
    {
        _playerTransform = GlobalController.Instance.player.GetComponent<Transform>();
        _transform       = transform;
        _rigidbody       = GetComponent<Rigidbody2D>();
        _animator        = GetComponent<Animator>();
        _spriteRenderer  = GetComponent<SpriteRenderer>();

        if (maxHealth <= 0) maxHealth = health;

        _currentState = new WardenIdle();
    }

    private void Update()
    {
        if (health <= 0) return;

        _playerEnemyDistance = _playerTransform.position.x - _transform.position.x;
        FacePlayer();
        TickCooldowns();
        CheckPhaseTransition();

        // State machine
        if (!_currentState.checkValid(this))
        {
            float dist = Mathf.Abs(_playerEnemyDistance);

            if (dist > detectDistance)
            {
                if (_inCombat) OnExitCombat();
                _currentState = new WardenIdle();
            }
            else
            {
                if (!_inCombat) OnEnterCombat();
                _currentState = ChooseState(dist);
            }
        }

        if (!_isActionLocked)
            _currentState.Execute(this);
    }

    // ── Phase management ───────────────────────────────────────────────────────

    private void CheckPhaseTransition()
    {
        float hpPct = (float)health / maxHealth;

        if (_phase == 1 && hpPct <= phaseTwoThreshold)
        {
            _phase = 2;
            _animator.SetTrigger("phaseTwo");
            StartCoroutine(PhaseTransitionPause(1.2f));
        }
        else if (_phase == 2 && hpPct <= phaseThreeThreshold)
        {
            _phase = 3;
            _animator.SetTrigger("phaseThree");
            StartCoroutine(PhaseTransitionPause(1.5f));
        }
    }

    private IEnumerator PhaseTransitionPause(float duration)
    {
        _isActionLocked = true;
        StopMoving();
        yield return new WaitForSeconds(duration);
        _isActionLocked = false;
    }

    // ── State selection ────────────────────────────────────────────────────────

    private State ChooseState(float dist)
    {
        // If the player is reachable by melee, prefer attacking
        if (dist <= meleeRange || dist <= chargeRange)
            return new WardenAttack();

        // Far away — if ranged is unlocked in phase 3 and ready, use it
        if (_phase >= 3 && _cdAshBarrage <= 0)
            return new WardenAttack();

        return new WardenChase();
    }

    // ── Cooldown ticks ─────────────────────────────────────────────────────────

    private void TickCooldowns()
    {
        float dt = Time.deltaTime;
        _cdGroundSlam  = Mathf.Max(0, _cdGroundSlam  - dt);
        _cdCharge      = Mathf.Max(0, _cdCharge      - dt);
        _cdSpin        = Mathf.Max(0, _cdSpin        - dt);
        _cdLeap        = Mathf.Max(0, _cdLeap        - dt);
        _cdAshBarrage  = Mathf.Max(0, _cdAshBarrage  - dt);
        _cdFuryCombo   = Mathf.Max(0, _cdFuryCombo   - dt);
    }

    // ── Attack selection (weighted, no repeat) ─────────────────────────────────

    /// <summary>
    /// Builds a weighted list of ready attacks for the current phase and
    /// player distance, then picks one — never the same as last time.
    /// </summary>
    public void TrySelectAndLaunchAttack()
    {
        if (_isActionLocked) return;

        float dist = Mathf.Abs(_playerEnemyDistance);

        var candidates = new List<(AttackType type, float weight)>();

        // Always available (phase 1+)
        if (_cdGroundSlam <= 0 && dist <= meleeRange)
            candidates.Add((AttackType.GroundSlam, 3f));

        if (_cdCharge <= 0 && dist > meleeRange && dist <= chargeRange)
            candidates.Add((AttackType.ChargeThrust, 3f));

        // Phase 2+
        if (_phase >= 2)
        {
            if (_cdSpin <= 0 && dist <= meleeRange)
                candidates.Add((AttackType.SpinSlash, 2.5f));

            if (_cdLeap <= 0 && dist > meleeRange && dist <= chargeRange)
                candidates.Add((AttackType.LeapSlam, 2.5f));
        }

        // Phase 3
        if (_phase >= 3)
        {
            if (_cdAshBarrage <= 0 && dist > chargeRange)
                candidates.Add((AttackType.AshBarrage, 4f));

            if (_cdFuryCombo <= 0 && dist <= meleeRange)
                candidates.Add((AttackType.FuryCombo, 4f));
        }

        // Remove last attack from candidates to avoid repeat
        candidates.RemoveAll(c => c.type == _lastAttack);

        if (candidates.Count == 0) return; // everything on cooldown

        // Weighted random pick
        float totalWeight = 0f;
        foreach (var c in candidates) totalWeight += c.weight;

        float roll = UnityEngine.Random.Range(0f, totalWeight);
        float acc  = 0f;
        AttackType chosen = candidates[0].type;
        foreach (var c in candidates)
        {
            acc += c.weight;
            if (roll <= acc) { chosen = c.type; break; }
        }

        _lastAttack = chosen;
        LaunchAttack(chosen);
    }

    private void LaunchAttack(AttackType attack)
    {
        switch (attack)
        {
            case AttackType.GroundSlam:   StartCoroutine(GroundSlamRoutine());   break;
            case AttackType.ChargeThrust: StartCoroutine(ChargeThrustRoutine()); break;
            case AttackType.SpinSlash:    StartCoroutine(SpinSlashRoutine());    break;
            case AttackType.LeapSlam:     StartCoroutine(LeapSlamRoutine());     break;
            case AttackType.AshBarrage:   StartCoroutine(AshBarrageRoutine());   break;
            case AttackType.FuryCombo:    StartCoroutine(FuryComboRoutine());    break;
        }
    }

    // ── Attack routines ────────────────────────────────────────────────────────

    /// <summary>
    /// GroundSlam — wind-up, pause, slam the ground, shockwave hitbox forward.
    /// </summary>
    private IEnumerator GroundSlamRoutine()
    {
        _isActionLocked = true;
        _cdGroundSlam   = groundSlamCooldown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("groundSlamStart");
        yield return new WaitForSeconds(0.5f);  // wind-up

        _animator.SetTrigger("groundSlamImpact");
        yield return new WaitForSeconds(0.15f); // impact frame

        // Forward AOE box
        CheckBoxHit(meleePoint.position, meleeBoxSize, groundSlamDamage);

        yield return new WaitForSeconds(0.5f);  // recovery
        _isActionLocked = false;
    }

    /// <summary>
    /// ChargeThrust — dash in the player's direction, stop, stab hitbox.
    /// Boss is invulnerable during the dash (handled by animation events or
    /// a separate invincibility layer — wire up as needed).
    /// </summary>
    private IEnumerator ChargeThrustRoutine()
    {
        _isActionLocked = true;
        _cdCharge       = chargeCooldown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("chargeThrustStart");
        yield return new WaitForSeconds(0.3f);  // charge stance

        // Snap direction toward player before dashing
        float dir = Mathf.Sign(_playerEnemyDistance);
        float dashDuration = 0.35f;
        float elapsed = 0f;

        while (elapsed < dashDuration)
        {
            elapsed += Time.deltaTime;
            Vector2 vel = _rigidbody.velocity;
            vel.x = dir * chargeSpeed;
            _rigidbody.velocity = vel;
            yield return null;
        }

        StopMoving();
        _animator.SetTrigger("chargeThrustImpact");
        yield return new WaitForSeconds(0.1f);  // stab frame

        CheckBoxHit(meleePoint.position, meleeBoxSize, chargeDamage);

        yield return new WaitForSeconds(0.45f); // recovery
        _isActionLocked = false;
    }

    /// <summary>
    /// SpinSlash (phase 2+) — full spin sweep, hits both sides with a circle cast.
    /// </summary>
    private IEnumerator SpinSlashRoutine()
    {
        _isActionLocked = true;
        _cdSpin         = spinCooldown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("spinSlashStart");
        yield return new WaitForSeconds(0.2f);  // start-up

        // 360° hit — overlap circle
        Collider2D hit = Physics2D.OverlapCircle(spinCenter.position, spinRadius, playerLayer);
        if (hit != null)
            hit.GetComponent<PlayerController>()?.hurt(spinDamage);

        _animator.SetTrigger("spinSlashEnd");
        yield return new WaitForSeconds(0.5f);  // recovery
        _isActionLocked = false;
    }

    /// <summary>
    /// LeapSlam (phase 2+) — boss launches toward player's X, lands with a crater AOE.
    /// </summary>
    private IEnumerator LeapSlamRoutine()
    {
        _isActionLocked = true;
        _cdLeap         = leapCooldown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("leapStart");
        yield return new WaitForSeconds(0.2f);  // jump squat

        // Leap impulse toward player
        float dir = Mathf.Sign(_playerEnemyDistance);
        _rigidbody.AddForce(new Vector2(dir * leapForce * 0.6f, leapForce), ForceMode2D.Impulse);

        // Wait until roughly landed (simple timer; replace with ground-detect if you have it)
        yield return new WaitForSeconds(0.7f);

        StopMoving();
        _animator.SetTrigger("leapImpact");
        yield return new WaitForSeconds(0.1f);  // impact frame

        Collider2D hit = Physics2D.OverlapCircle(leapImpactPoint.position, leapRadius, playerLayer);
        if (hit != null)
            hit.GetComponent<PlayerController>()?.hurt(leapDamage);

        yield return new WaitForSeconds(0.5f);  // recovery
        _isActionLocked = false;
    }

    /// <summary>
    /// AshBarrage (phase 3) — fires 3 projectiles at the player with a short delay.
    /// </summary>
    private IEnumerator AshBarrageRoutine()
    {
        _isActionLocked = true;
        _cdAshBarrage   = ashBarrageCooldown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("ashBarrageStart");
        yield return new WaitForSeconds(0.3f);

        for (int i = 0; i < 3; i++)
        {
            SpawnAshProjectile();
            yield return new WaitForSeconds(0.3f);
        }

        yield return new WaitForSeconds(0.3f);  // recovery
        _isActionLocked = false;
    }

    /// <summary>
    /// FuryCombo (phase 3) — 3-hit melee combo, each hit has its own hitbox window.
    /// </summary>
    private IEnumerator FuryComboRoutine()
    {
        _isActionLocked = true;
        _cdFuryCombo    = furyComboColdown * PhaseSpeedMultiplier();

        StopMoving();
        _animator.SetTrigger("furyComboStart");

        // Hit 1
        yield return new WaitForSeconds(0.2f);
        CheckBoxHit(meleePoint.position, meleeBoxSize, furyHitDamage);

        // Hit 2
        _animator.SetTrigger("furyComboHit2");
        yield return new WaitForSeconds(0.18f);
        CheckBoxHit(meleePoint.position, meleeBoxSize, furyHitDamage);

        // Hit 3 (fastest)
        _animator.SetTrigger("furyComboHit3");
        yield return new WaitForSeconds(0.15f);
        CheckBoxHit(meleePoint.position, meleeBoxSize, furyHitDamage);

        yield return new WaitForSeconds(0.5f);  // recovery
        _isActionLocked = false;
    }

    // ── Helper utilities ───────────────────────────────────────────────────────

    /// <summary>Phase 3 attacks 30% faster, phase 2 10% faster.</summary>
    private float PhaseSpeedMultiplier() =>
        _phase == 3 ? 0.7f : _phase == 2 ? 0.9f : 1f;

    private void FacePlayer()
    {
        int dir = _playerEnemyDistance > 0 ? 1 : _playerEnemyDistance < 0 ? -1 : 0;
        if (dir == 0) return;
        Vector3 s = _transform.localScale;
        s.x = dir;
        _transform.localScale = s;
    }

    public void MoveToPlayer()
    {
        float dir = Mathf.Abs(_playerEnemyDistance) < 0.1f ? 0 : Mathf.Sign(_playerEnemyDistance);
        float spd = walkSpeed * (_phase >= 2 ? 1.2f : 1f) * (_phase >= 3 ? 1.1f : 1f);
        Vector2 vel = _rigidbody.velocity;
        vel.x = dir * spd;
        _rigidbody.velocity = vel;
        _animator.SetFloat("Speed", Mathf.Abs(vel.x));
    }

    public void StopMoving()
    {
        Vector2 vel = _rigidbody.velocity;
        vel.x = 0;
        _rigidbody.velocity = vel;
        _animator.SetFloat("Speed", 0);
    }

    private void CheckBoxHit(Vector2 center, Vector2 size, int damage)
    {
        Collider2D hit = Physics2D.OverlapBox(center, size, 0f, playerLayer);
        if (hit != null)
            hit.GetComponent<PlayerController>()?.hurt(damage);
    }

    private void SpawnAshProjectile()
    {
        if (ashProjectilePrefab == null || projectileSpawnPoint == null) return;

        Vector2 dir = ((Vector2)_playerTransform.position - (Vector2)projectileSpawnPoint.position).normalized;
        GameObject proj = Instantiate(ashProjectilePrefab, projectileSpawnPoint.position, Quaternion.identity);

        // Assumes your projectile has an AshProjectile component with Init(dir, damage)
        proj.GetComponent<AshProjectile>()?.Init(dir, ashProjectileDmg);
    }

    // ── Hurt / Die ─────────────────────────────────────────────────────────────

    public override void hurt(int damage)
    {
        health = Math.Max(health - damage, 0);
        BossHPUI.Instance?.UpdateHP(health);

        if (health == 0) { die(); return; }

        // Briefly lock action for hurt flash — don't cancel a charge mid-dash
        if (!(_lastAttack == AttackType.ChargeThrust))
            StartCoroutine(HurtFlash());
    }

    private IEnumerator HurtFlash()
    {
        _isActionLocked = true;
        Vector2 recoil = hurtRecoil;
        recoil.x *= -_transform.localScale.x;
        _rigidbody.velocity = recoil;
        yield return new WaitForSeconds(hurtRecoilTime);
        _isActionLocked = false;
        StopMoving();
    }

    protected override void die()
    {
        StopAllCoroutines();
        _animator.SetTrigger("isDead");
        _rigidbody.velocity = Vector2.zero;
        gameObject.layer = LayerMask.NameToLayer("Decoration");

        Vector2 force;
        force.x = _transform.localScale.x * deathForce.x;
        force.y = deathForce.y;
        _rigidbody.AddForce(force, ForceMode2D.Impulse);

        BossHPUI.Instance?.Hide();
        StartCoroutine(FadeAndDestroy());
    }

    // ── Combat enter/exit ──────────────────────────────────────────────────────

    private void OnEnterCombat()
    {
        _inCombat = true;
        BossHPUI.Instance?.Show(bossDisplayName, maxHealth);
    }

    private void OnExitCombat()
    {
        _inCombat = false;
        BossHPUI.Instance?.Hide();
    }

    // ── Fade coroutine ─────────────────────────────────────────────────────────

    private IEnumerator FadeAndDestroy()
    {
        float timer = destroyDelay;
        while (timer > 0)
        {
            timer -= Time.deltaTime;
            Color c = _spriteRenderer.color;
            c.a = Mathf.Max(0, c.a - Time.deltaTime / destroyDelay);
            _spriteRenderer.color = c;
            yield return null;
        }
        Destroy(gameObject);
    }

    // ── States ─────────────────────────────────────────────────────────────────

    public class WardenIdle : State
    {
        public override bool checkValid(EnemyController e) =>
            Mathf.Abs(e.playerEnemyDistance()) > e.detectDistance;

        public override void Execute(EnemyController e)
            => ((WardenController)e).StopMoving();
    }

    public class WardenChase : State
    {
        public override bool checkValid(EnemyController e)
        {
            var w   = (WardenController)e;
            float d = Mathf.Abs(e.playerEnemyDistance());
            return d <= e.detectDistance && d > w.meleeRange &&
                   !(w._phase >= 3 && w._cdAshBarrage <= 0);
        }

        public override void Execute(EnemyController e)
            => ((WardenController)e).MoveToPlayer();
    }

    public class WardenAttack : State
    {
        public override bool checkValid(EnemyController e)
        {
            var w   = (WardenController)e;
            float d = Mathf.Abs(e.playerEnemyDistance());
            return d <= e.detectDistance &&
                   (d <= w.chargeRange || (w._phase >= 3 && w._cdAshBarrage <= 0));
        }

        public override void Execute(EnemyController e)
            => ((WardenController)e).TrySelectAndLaunchAttack();
    }

    // ── Gizmos ─────────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (meleePoint != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(meleePoint.position, meleeBoxSize);
        }
        if (spinCenter != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(spinCenter.position, spinRadius);
        }
        if (leapImpactPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(leapImpactPoint.position, leapRadius);
        }
    }

    public override float behaveInterval()
    {
        return 1f;
    }
}
