using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ┌─────────────────────────────────────────────────────────────────────────┐
// │  LostVesselController                                                   │
// │                                                                         │
// │  A redesign of the boss pattern to replace the nested State-class FSM   │
// │  with a clean data-driven approach:                                     │
// │                                                                         │
// │  • ONE coroutine owns the entire boss brain — no more per-frame         │
// │    Execute() fighting with _isActionLocked booleans.                    │
// │  • Attacks are AttackData structs: range, cooldown, weight, phase.      │
// │  • Selection is a weighted random draw with no-repeat guarantee.        │
// │  • Animator is driven only from one place (the attack coroutines),      │
// │    never from Update() — no trigger races.                              │
// │  • Phase 2 doubles the fire-rate and adds the FireProjectile attack.    │
// │                                                                         │
// │  ── Animator triggers required ──────────────────────────────────────── │
// │  "walk"           Bool — true while chasing                             │
// │  "idle"           Trigger — snap to idle                                │
// │  "jumpAttack"     Trigger — plays jump + land clip                      │
// │  "slashForward"   Trigger — first half of back-and-forth slash          │
// │  "slashBack"      Trigger — second half of back-and-forth slash         │
// │  "chargeStart"    Trigger — charge wind-up                              │
// │  "chargeImpact"   Trigger — charge hit / stop                           │
// │  "fireProjectile" Trigger — fire orb cast animation                     │
// │  "phaseTwo"       Trigger — phase transition flash                      │
// │  "hurt"           Trigger                                               │
// │  "death"          Trigger                                               │
// └─────────────────────────────────────────────────────────────────────────┘

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(SpriteRenderer))]
public class LostVesselController : EnemyController
{
    // ── Inspector ──────────────────────────────────────────────────────────

    [Header("Identity")]
    [SerializeField] private string bossDisplayName = "Lost Vessel";

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 3.5f;
    [SerializeField] private float chargeSpeed = 16f;
    [SerializeField] private float jumpForceX = 6f;
    [SerializeField] private float jumpForceY = 18f;

    [Header("Ranges")]
    [SerializeField] private float meleeRange = 2.8f;   // slash, jump
    [SerializeField] private float chargeRange = 9f;     // charge
    [SerializeField] private float fireRange = 14f;    // projectile (phase 2)

    [Header("Timing (seconds)")]
    [SerializeField] private float slashWindup = 0.25f;
    [SerializeField] private float slashActiveWindow = 0.18f;
    [SerializeField] private float slashRecovery = 0.35f;
    [SerializeField] private float slashGapBetween = 0.12f;   // pause between the two slashes
    [SerializeField] private float jumpRiseTime = 0.45f;
    [SerializeField] private float jumpFallTime = 0.30f;
    [SerializeField] private float jumpRecovery = 0.40f;
    [SerializeField] private float chargeWindup = 0.35f;
    [SerializeField] private float chargeDuration = 0.40f;
    [SerializeField] private float chargeRecovery = 0.50f;
    [SerializeField] private float fireWindup = 0.30f;
    [SerializeField] private float fireRecovery = 0.40f;
    [SerializeField] private float betweenAttacks = 0.6f;    // rest gap after every attack

    [Header("Damage")]
    [SerializeField] private int slashDamage = 14;
    [SerializeField] private int jumpDamage = 20;
    [SerializeField] private int chargeDamage = 18;

    [Header("Hitboxes")]
    [SerializeField] private Transform meleePoint;
    [SerializeField] private Vector2 meleeBoxSize = new Vector2(4.5f, 1.4f);
    [SerializeField] private LayerMask playerLayer;

    [Header("Projectile")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private Transform projectileSpawn;

    [Header("Phase 2")]
    [SerializeField][Range(0f, 1f)] private float phaseTwoThreshold = 0.5f;
    [SerializeField] private float phase2SpeedMult = 1.3f;   // movement speed bonus
    [SerializeField] private float phase2TimingMult = 0.75f;  // attack timings × this
    [SerializeField] private float phase2BetweenMult = 0.65f;  // rest gap × this

    [Header("Misc")]
    public int maxHealth;

    // ── Private state ──────────────────────────────────────────────────────

    private bool _isPhaseTwo;
    private bool _inCombat;
    private bool _dead;

    // The single coroutine handle for the whole boss brain
    private Coroutine _brainCoroutine;

    private Transform _playerTransform;
    private Rigidbody2D _rb;
    private Animator _anim;
    private SpriteRenderer _sr;

    // ── Attack catalogue ───────────────────────────────────────────────────

    // Each entry: (id, minRange, maxRange, baseWeight, requiresPhase2)
    // Range is the absolute x-distance to player.
    // Weight is the raw selection weight — higher = more likely.
    private readonly struct AttackDef
    {
        public readonly int Id;
        public readonly float MinRange;    // inclusive lower bound
        public readonly float MaxRange;    // inclusive upper bound (float.MaxValue = no limit)
        public readonly float Weight;
        public readonly bool Phase2Only;
        public AttackDef(int id, float min, float max, float weight, bool p2 = false)
        { Id = id; MinRange = min; MaxRange = max; Weight = weight; Phase2Only = p2; }
    }

    private static readonly int ATK_SLASH = 0;
    private static readonly int ATK_JUMP = 1;
    private static readonly int ATK_CHARGE = 2;
    private static readonly int ATK_FIRE = 3;

    // Slash: inside melee range
    // Jump:  inside melee range (different flavour, preferred over plain slash)
    // Charge: mid range — too far to slash, close enough to reach
    // Fire:   any range, phase 2 only
    private readonly AttackDef[] _attacks = {
        new AttackDef(ATK_SLASH,  0f,   2.8f,  3f),
        new AttackDef(ATK_JUMP,   0f,   2.8f,  2f),
        new AttackDef(ATK_CHARGE, 2.8f, 9f,    3f),
        new AttackDef(ATK_FIRE,   0f,   float.MaxValue, 2.5f, p2: true),
    };

    private int _lastAttack = -1;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Start()
    {
        _playerTransform = GlobalController.Instance.player.GetComponent<Transform>();
        _rb = GetComponent<Rigidbody2D>();
        _anim = GetComponent<Animator>();
        _sr = GetComponent<SpriteRenderer>();

        if (maxHealth <= 0) maxHealth = health;

        _brainCoroutine = StartCoroutine(BossBrain());
    }

    private void Update()
    {
        if (_dead) return;
        _playerEnemyDistance = _playerTransform.position.x - transform.position.x;
        FacePlayer();
        CheckPhaseTransition();
    }

    // ══════════════════════════════════════════════════════════════════════
    // BOSS BRAIN — one coroutine, one loop, no booleans fighting each other
    // ══════════════════════════════════════════════════════════════════════

    private IEnumerator BossBrain()
    {
        // Wait until player enters detection range
        while (Mathf.Abs(_playerEnemyDistance) > detectDistance)
            yield return null;

        OnEnterCombat();

        while (!_dead)
        {
            float dist = Mathf.Abs(_playerEnemyDistance);

            // ── Idle / lost player ─────────────────────────────────────
            if (dist > detectDistance)
            {
                SetWalking(false);
                yield return new WaitUntil(() =>
                    Mathf.Abs(_playerEnemyDistance) <= detectDistance || _dead);
                continue;
            }

            // ── Pick an attack ─────────────────────────────────────────
            int chosen = ChooseAttack(dist);

            if (chosen == -1)
            {
                // No attack ready — chase until in range
                yield return StartCoroutine(ChaseUntilInRange());
                continue;
            }

            // ── Execute chosen attack ──────────────────────────────────
            SetWalking(false);
            yield return chosen switch
            {
                var x when x == ATK_SLASH => StartCoroutine(DoSlashCombo()),
                var x when x == ATK_JUMP => StartCoroutine(DoJumpAttack()),
                var x when x == ATK_CHARGE => StartCoroutine(DoCharge()),
                var x when x == ATK_FIRE => StartCoroutine(DoFireProjectile()),
                _ => null
            };

            _lastAttack = chosen;

            // ── Mandatory rest between attacks ─────────────────────────
            float rest = betweenAttacks * (_isPhaseTwo ? phase2BetweenMult : 1f);
            yield return new WaitForSeconds(rest);
        }
    }

    // ── Chase coroutine ────────────────────────────────────────────────────

    private IEnumerator ChaseUntilInRange()
    {
        SetWalking(true);

        while (!_dead)
        {
            float dist = Mathf.Abs(_playerEnemyDistance);

            // Stop chasing if we're now in range of any available attack
            if (ChooseAttack(dist) != -1) break;

            // Stop if player left detection range
            if (dist > detectDistance) break;

            MoveTowardPlayer();
            yield return null;
        }

        SetWalking(false);
        StopMoving();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ATTACK ROUTINES
    // ══════════════════════════════════════════════════════════════════════

    // ── Back-and-forth slash combo ─────────────────────────────────────────
    // Lost Vessel does a forward slash then immediately reverses for a back slash.
    // Both hits use the same melee hitbox; the sprite flips between them.

    private IEnumerator DoSlashCombo()
    {
        float t = _isPhaseTwo ? phase2TimingMult : 1f;

        // ── Forward slash ──────────────────────────────────────────────
        _anim.SetTrigger("slashForward");
        yield return new WaitForSeconds(slashWindup * t);
        CheckBoxHit(slashDamage);
        yield return new WaitForSeconds(slashActiveWindow * t);

        // ── Gap between hits ───────────────────────────────────────────
        yield return new WaitForSeconds(slashGapBetween * t);

        // ── Back slash — flip, then hit ────────────────────────────────
        FlipInstant();
        _anim.SetTrigger("slashBack");
        yield return new WaitForSeconds(slashWindup * t);
        CheckBoxHit(slashDamage);
        yield return new WaitForSeconds(slashActiveWindow * t);

        // ── Flip back to face player after combo ───────────────────────
        FacePlayer();
        yield return new WaitForSeconds(slashRecovery * t);
    }

    // ── Jump attack ────────────────────────────────────────────────────────
    // Launches toward the player's X position, hits on landing.

    private IEnumerator DoJumpAttack()
    {
        float t = _isPhaseTwo ? phase2TimingMult : 1f;

        _anim.SetTrigger("jumpAttack");

        // Jump impulse — aim for where the player is right now
        float dir = Mathf.Sign(_playerEnemyDistance);
        _rb.AddForce(new Vector2(dir * jumpForceX, jumpForceY), ForceMode2D.Impulse);

        yield return new WaitForSeconds(jumpRiseTime * t);   // arc up

        // Slam down — force downward to accelerate landing
        _rb.velocity = new Vector2(_rb.velocity.x, -jumpForceY * 1.5f);

        yield return new WaitForSeconds(jumpFallTime * t);   // fall
        StopMoving();
        CheckBoxHit(jumpDamage);                             // impact hit

        yield return new WaitForSeconds(jumpRecovery * t);   // land recovery
    }

    // ── Charge attack ──────────────────────────────────────────────────────
    // Winds up, dashes across the screen at high speed, stops and hits.

    private IEnumerator DoCharge()
    {
        float t = _isPhaseTwo ? phase2TimingMult : 1f;
        float spd = chargeSpeed * (_isPhaseTwo ? phase2SpeedMult : 1f);

        _anim.SetTrigger("chargeStart");
        yield return new WaitForSeconds(chargeWindup * t);   // wind-up — telegraphed

        // Snapshot direction at dash start so it commits
        float dir = Mathf.Sign(_playerEnemyDistance);
        float elapsed = 0f;

        while (elapsed < chargeDuration * t)
        {
            elapsed += Time.deltaTime;
            Vector2 vel = _rb.velocity;
            vel.x = dir * spd;
            _rb.velocity = vel;
            yield return null;
        }

        StopMoving();
        _anim.SetTrigger("chargeImpact");
        CheckBoxHit(chargeDamage);

        yield return new WaitForSeconds(chargeRecovery * t);
    }

    // ── Fire projectile (phase 2) ──────────────────────────────────────────

    private IEnumerator DoFireProjectile()
    {
        float t = phase2TimingMult;   // always fast since it's phase 2 only

        StopMoving();
        _anim.SetTrigger("fireProjectile");
        yield return new WaitForSeconds(fireWindup * t);

        SpawnProjectile();

        yield return new WaitForSeconds(fireRecovery * t);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ATTACK SELECTION
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Weighted random pick from all attacks that:
    ///   1. Are valid for the current distance
    ///   2. Are available in the current phase
    ///   3. Were not the last attack used (no-repeat)
    /// Returns -1 if nothing is eligible (should chase).
    /// </summary>
    private int ChooseAttack(float dist)
    {
        var pool = new List<(int id, float weight)>();

        foreach (var atk in _attacks)
        {
            if (atk.Phase2Only && !_isPhaseTwo) continue;
            if (dist < atk.MinRange || dist > atk.MaxRange) continue;
            if (atk.Id == _lastAttack) continue;   // no repeat
            pool.Add((atk.Id, atk.Weight));
        }

        if (pool.Count == 0) return -1;

        float total = 0f;
        foreach (var (_, w) in pool) total += w;

        float roll = UnityEngine.Random.Range(0f, total);
        float acc = 0f;
        foreach (var (id, w) in pool)
        {
            acc += w;
            if (roll <= acc) return id;
        }

        return pool[pool.Count - 1].id;
    }

    // ══════════════════════════════════════════════════════════════════════
    // HURT / DEATH  (called by player projectile / sword)
    // ══════════════════════════════════════════════════════════════════════

    public override void hurt(int damage)
    {
        if (_dead) return;

        health = Mathf.Max(health - damage, 0);
        BossHPUI.Instance?.UpdateHP(health);

        if (health == 0) { Die(); return; }

        // Don't stagger during charge — it would leave the boss sliding forever
        if (_lastAttack == ATK_CHARGE) return;

        StopBrain();
        StartCoroutine(HurtStagger());
    }

    private IEnumerator HurtStagger()
    {
        _anim.SetTrigger("hurt");

        Vector2 recoil = hurtRecoil;
        recoil.x *= -Mathf.Sign(transform.localScale.x);
        _rb.velocity = recoil;

        yield return new WaitForSeconds(hurtRecoilTime);

        StopMoving();
        _brainCoroutine = StartCoroutine(BossBrain());
    }

    private void Die()
    {
        _dead = true;
        StopBrain();

        _anim.SetTrigger("death");
        _rb.velocity = Vector2.zero;
        gameObject.layer = LayerMask.NameToLayer("Decoration");
        _rb.AddForce(new Vector2(transform.localScale.x * deathForce.x, deathForce.y),
                     ForceMode2D.Impulse);

        BossHPUI.Instance?.Hide();
        StartCoroutine(FadeAndDestroy());
    }

    // ══════════════════════════════════════════════════════════════════════
    // PHASE TRANSITION
    // ══════════════════════════════════════════════════════════════════════

    private void CheckPhaseTransition()
    {
        if (_isPhaseTwo || _dead) return;
        if ((float)health / maxHealth > phaseTwoThreshold) return;

        _isPhaseTwo = true;
        StopBrain();
        StartCoroutine(PhaseTransition());
    }

    private IEnumerator PhaseTransition()
    {
        StopMoving();
        _anim.SetTrigger("phaseTwo");
        yield return new WaitForSeconds(1.5f);   // let the flash / roar anim play
        _brainCoroutine = StartCoroutine(BossBrain());
    }

    // ══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════

    private void StopBrain()
    {
        if (_brainCoroutine != null)
            StopCoroutine(_brainCoroutine);
        _brainCoroutine = null;
    }

    private void FacePlayer()
    {
        float d = _playerEnemyDistance;
        if (Mathf.Abs(d) < 0.05f) return;
        Vector3 s = transform.localScale;
        s.x = d > 0 ? 1 : -1;
        transform.localScale = s;
    }

    // Instantly flips facing without reading player position (used between slash hits)
    private void FlipInstant()
    {
        Vector3 s = transform.localScale;
        s.x = -s.x;
        transform.localScale = s;
    }

    private void MoveTowardPlayer()
    {
        float dir = Mathf.Abs(_playerEnemyDistance) < 0.1f
            ? 0 : Mathf.Sign(_playerEnemyDistance);
        float spd = walkSpeed * (_isPhaseTwo ? phase2SpeedMult : 1f);
        Vector2 vel = _rb.velocity;
        vel.x = dir * spd;
        _rb.velocity = vel;
    }

    private void StopMoving()
    {
        Vector2 vel = _rb.velocity;
        vel.x = 0;
        _rb.velocity = vel;
    }

    private void SetWalking(bool walking)
    {
        _anim.SetBool("walk", walking);
    }

    private void CheckBoxHit(int damage)
    {
        if (meleePoint == null) return;
        Collider2D hit = Physics2D.OverlapBox(meleePoint.position, meleeBoxSize, 0f, playerLayer);
        if (hit != null)
            hit.GetComponent<PlayerController>()?.hurt(damage);
    }

    private void SpawnProjectile()
    {
        if (projectilePrefab == null || projectileSpawn == null) return;
        Vector2 dir = ((Vector2)_playerTransform.position
                       - (Vector2)projectileSpawn.position).normalized;
        var go = Instantiate(projectilePrefab, projectileSpawn.position, Quaternion.identity);
        go.GetComponent<AshProjectile>()?.Init(dir, 12);
    }

    private void OnEnterCombat()
    {
        _inCombat = true;
        BossHPUI.Instance?.Show(bossDisplayName, maxHealth);
    }

    private IEnumerator FadeAndDestroy()
    {
        float timer = destroyDelay;
        while (timer > 0)
        {
            timer -= Time.deltaTime;
            Color c = _sr.color;
            c.a = Mathf.Max(0, c.a - Time.deltaTime / destroyDelay);
            _sr.color = c;
            yield return null;
        }
        Destroy(gameObject);
    }

    // ── Gizmos ─────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (meleePoint == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(meleePoint.position, meleeBoxSize);

        // Draw range rings
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, meleeRange);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, chargeRange);
    }

    public override float behaveInterval()
    {
        return 1f;
    }
    protected override void die()
    {
        return;
    }
}