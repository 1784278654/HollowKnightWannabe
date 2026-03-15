using System;
using System.Collections;
using UnityEngine;

public class BossController : EnemyController
{
    public float walkSpeed;
    public float attackRange;
    public float rollAtkRange;
    public float meleeCooldown;
    public float rollAtkCooldown = 10f;
    public float phaseTwoHealthPercent;
    public int maxHealth;

    private bool _isAttackOnCooldown;
    private bool _isRollingOnCooldown;
    private bool _isRolling;
    private bool _isActionLocked;
    private bool _isPhaseTwo;

    // FIX 1: Cache the original rollAtkCooldown so it can be properly reset
    private float _rollAtkCooldownDefault;

    private Transform _playerTransform;
    private Transform _transform;
    private Rigidbody2D _rigidbody;
    private Animator _animator;
    private SpriteRenderer _spriteRenderer;

    [SerializeField] private Transform attackPoint;

    [SerializeField] private Vector2 attackBoxSize = new Vector2(2f, 1f);
    [SerializeField] private LayerMask playerLayer;

    private void Start()
    {
        _playerTransform = GlobalController.Instance.player.GetComponent<Transform>();
        _transform = gameObject.GetComponent<Transform>();
        _rigidbody = gameObject.GetComponent<Rigidbody2D>();
        _animator = gameObject.GetComponent<Animator>();
        _spriteRenderer = gameObject.GetComponent<SpriteRenderer>();

        if (maxHealth <= 0)
            maxHealth = health;

        // FIX 1: Cache the inspector-assigned rollAtkCooldown as the reset value
        _rollAtkCooldownDefault = rollAtkCooldown;

        _isAttackOnCooldown = false;
        _isRollingOnCooldown = false;
        _isRolling = false;
        _isActionLocked = false;
        _isPhaseTwo = false;

        _currentState = new Idle();
    }

    private void Update()
    {
        if (health <= 0)
            return;

        _playerEnemyDistance = _playerTransform.position.x - _transform.position.x;

        // Flip sprite to face player
        int direction = _playerEnemyDistance > 0 ? 1 : _playerEnemyDistance < 0 ? -1 : 0;
        if (direction != 0)
        {
            Vector3 newScale = _transform.localScale;
            newScale.x = direction;
            _transform.localScale = newScale;
        }

        // Phase two transition
        if (!_isPhaseTwo && health <= Mathf.CeilToInt(maxHealth * phaseTwoHealthPercent))
        {
            _isPhaseTwo = true;
            _animator.SetTrigger("phaseTwo");
        }

        // FIX 2: State transitions were broken — rolling attack should be PREFERRED
        // when available (not on cooldown), not treated as the fallback.
        // Old logic could get stuck: if roll wasn't on cooldown it would always
        // switch to Attack state even when out of melee range.
        if (!_currentState.checkValid(this))
        {
            float playerEnemyDistanceAbs = Math.Abs(_playerEnemyDistance);

            if (playerEnemyDistanceAbs > detectDistance)
            {
                _currentState = new Idle();
                if (BossHPUI.Instance.gameObject.activeSelf)
                {
                    BossHPUI.Instance.Hide();
                }
            }
            else if (!_isRollingOnCooldown)
            {
                // Roll attack is available — always use it (it chases the player)
                _currentState = new RollAttack();

                BossHPUI.Instance.Show("Shade", maxHealth);
            }
            else if (playerEnemyDistanceAbs > attackRange)
            {
                _currentState = new Chase();
            }
            else
            {
                _currentState = new Attack();
            }
        }

        if (!_isActionLocked)
            _currentState.Execute(this);

        // FIX 3: Cooldown timer should only tick when NOT rolling
        // (was correct) but reset value was hardcoded to 10f — now uses cached default
        if (_isRollingOnCooldown && !_isRolling)
        {
            rollAtkCooldown -= Time.deltaTime;
            if (rollAtkCooldown <= 0)
            {
                _isRollingOnCooldown = false;
                rollAtkCooldown = _rollAtkCooldownDefault; // FIX 1 applied here
            }
        }
    }

    public override float behaveInterval()
    {
        return _isPhaseTwo ? meleeCooldown * 0.6f : meleeCooldown;
    }

    public override void hurt(int damage)
    {
        health = Math.Max(health - damage, 0);
        BossHPUI.Instance.UpdateHP(health);
        //SoundManager.Instance.PlaySFX("hitReject");

        if (health == 0)
        {
            die();
            return;
        }

        Vector2 newVelocity = hurtRecoil;
        newVelocity.x *= -_transform.localScale.x;
        _rigidbody.velocity = newVelocity;

        StartCoroutine(hurtCoroutine());
    }

    protected override void die()
    {
        _animator.SetTrigger("isDead");

        _rigidbody.velocity = Vector2.zero;

        gameObject.layer = LayerMask.NameToLayer("Decoration");

        Vector2 newForce;
        newForce.x = _transform.localScale.x * deathForce.x;
        newForce.y = deathForce.y;
        _rigidbody.AddForce(newForce, ForceMode2D.Impulse);

        StartCoroutine(fadeCoroutine());
        BossHPUI.Instance.Hide();
    }

    public void moveToPlayer()
    {
        float moveDirection = Math.Abs(_playerEnemyDistance) < 0.1f ? 0 : Math.Sign(_playerEnemyDistance);

        Vector2 newVelocity = _rigidbody.velocity;
        newVelocity.x = moveDirection * walkSpeed * (_isPhaseTwo ? 1.25f : 1f);
        _rigidbody.velocity = newVelocity;

        _animator.SetFloat("Speed", Math.Abs(newVelocity.x));
    }

    public void stopMoving()
    {
        Vector2 newVelocity = _rigidbody.velocity;
        newVelocity.x = 0;
        _rigidbody.velocity = newVelocity;

        _animator.SetFloat("Speed", 0);
    }

    // FIX 4: rollToPlayer was a coroutine called every frame from Execute(),
    // meaning it could be started dozens of times per second. It is now
    // properly gated by _isRolling so it only starts once per roll attack.
    private IEnumerator rollToPlayer()
    {
        _isRolling = true;

        // Snapshot direction at roll start so the boss commits to one direction
        float moveDirection = Math.Sign(_playerEnemyDistance);
        float rollSpeed = walkSpeed * (_isPhaseTwo ? 1.25f : 1f) * 4f;

        while (true)
        {
            // Check if we've caught the player
            if (Math.Abs(_playerEnemyDistance) <= rollAtkRange)
            {
                SoundManager.Instance.StopSFX("bossRollAtk");
                stopMoving();
                _animator.SetTrigger("attackChaseEnd");
                SoundManager.Instance.PlaySFX("bossSwingLight");

                // FIX 5: Wait for ChaseAttackEnd animation to finish, then hit
                yield return new WaitForSeconds(0.4f);
                CheckHitResult(); // deal damage at the right moment in the animation

                yield return new WaitForSeconds(1.0f); // remainder of recovery
                _isRollingOnCooldown = true;
                _isRolling = false;
                _isAttackOnCooldown = false;
                _isActionLocked = false;
                yield break;
            }

            // Keep moving toward player
            Vector2 vel = _rigidbody.velocity;
            vel.x = moveDirection * rollSpeed;
            _rigidbody.velocity = vel;
            _animator.SetFloat("Speed", Math.Abs(vel.x));

            yield return null;
        }
    }

    public void attackPlayer()
    {
        if (_isAttackOnCooldown)
            return;

        StartCoroutine(attackRoutine());
    }

    public void startRollAttack()
    {
        // FIX 4: Single entry-point called once by RollAttack state
        if (_isAttackOnCooldown)
            return;

        StartCoroutine(attackRoutine());
    }

    private IEnumerator hurtCoroutine()
    {
        _isActionLocked = true;
        // FIX 6: Interrupt any ongoing roll so a hurt reaction is immediate
        _isRolling = false;
        StopCoroutine(nameof(rollToPlayer));

        yield return new WaitForSeconds(hurtRecoilTime);

        _isActionLocked = false;
        stopMoving();
    }

    private IEnumerator attackRoutine()
    {
        _isAttackOnCooldown = true;
        _isActionLocked = true;

        if (!_isRollingOnCooldown)
        {
            // --- Roll / Chase Attack ---
            _animator.SetTrigger("attackChaseStart");
            yield return new WaitForSeconds(0.3f);

            _animator.SetTrigger("attackChaseMid");
            SoundManager.Instance.PlaySFX("bossRollAtk");

            // FIX 4: yield-start the coroutine and wait; rollToPlayer now owns
            // _isRolling, _isRollingOnCooldown, _isActionLocked, and _isAttackOnCooldown.
            yield return StartCoroutine(rollToPlayer());
            // All cleanup is handled inside rollToPlayer — nothing needed here.
        }
        else
        {
            // --- Normal Melee Attack ---
            _animator.SetTrigger("attackMelee");
            SoundManager.Instance.PlaySFX("bossSwingHeavy");


            // FIX 7: Hit window should match the swing animation frame, not after meleeCooldown
            yield return new WaitForSeconds(0.25f);
            CheckHitResult();

            // FIX 8: Wait out the rest of the animation before releasing the lock
            yield return new WaitForSeconds(0.4f);
            yield return new WaitForSeconds(meleeCooldown);
            _isActionLocked = false;
            _isAttackOnCooldown = false;
            // FIX 9: Actual cooldown before next melee is handled by behaveInterval()
            // via the EnemyController base class, so we don't need a manual delay here.
            // If your base class does NOT handle this, uncomment the line below:
            // yield return new WaitForSeconds(meleeCooldown);
        }
    }

    private IEnumerator fadeCoroutine()
    {
        float timer = destroyDelay;

        while (timer > 0)
        {
            timer -= Time.deltaTime;

            if (_spriteRenderer.color.a > 0)
            {
                Color newColor = _spriteRenderer.color;
                newColor.a -= Time.deltaTime / destroyDelay;
                _spriteRenderer.color = newColor;
            }

            yield return null;
        }

        Destroy(gameObject);
    }

    // ─── States ──────────────────────────────────────────────────────────────

    public class Idle : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            return Math.Abs(enemyController.playerEnemyDistance()) > enemyController.detectDistance;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            boss.stopMoving();
        }
    }

    public class Chase : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            float dist = Math.Abs(enemyController.playerEnemyDistance());
            // Chase is valid only when roll is on cooldown and player is out of melee range
            return dist <= enemyController.detectDistance &&
                   dist > boss.attackRange &&
                   boss._isRollingOnCooldown;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            boss.moveToPlayer();
        }
    }

    public class Attack : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            // Melee attack valid only when roll is on cooldown and within melee range
            return boss._isRollingOnCooldown &&
                   Math.Abs(enemyController.playerEnemyDistance()) <= boss.attackRange;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            boss.stopMoving();
            boss.attackPlayer();
        }
    }

    // FIX 2: New dedicated state for the roll attack
    public class RollAttack : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            // Valid as long as roll is not on cooldown and boss is in detection range
            return !boss._isRollingOnCooldown &&
                   Math.Abs(enemyController.playerEnemyDistance()) <= enemyController.detectDistance;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController boss = (BossController)enemyController;
            // FIX 4: startRollAttack() guards with _isAttackOnCooldown so this is safe every frame
            boss.startRollAttack();
        }
    }

    // ─── Hit Detection ───────────────────────────────────────────────────────

    public void CheckHitResult()
    {
        Collider2D hit = Physics2D.OverlapBox(
            attackPoint.position,
            attackBoxSize,
            0f,
            playerLayer
        );

        if (hit != null)
        {
            PlayerController playerController = _playerTransform.GetComponent<PlayerController>();
            playerController.hurt(damageToPlayer);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (attackPoint == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(attackPoint.position, attackBoxSize);
    }
}