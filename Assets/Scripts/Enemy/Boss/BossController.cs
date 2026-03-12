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

    private Transform _playerTransform;
    private Transform _transform;
    private Rigidbody2D _rigidbody;
    private Animator _animator;
    private SpriteRenderer _spriteRenderer;

    [SerializeField] private Transform attackPoint;
    [SerializeField] private Transform rollAtkPoint;

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

        int direction = _playerEnemyDistance > 0 ? 1 : _playerEnemyDistance < 0 ? -1 : 0;
        if (direction != 0)
        {
            Vector3 newScale = _transform.localScale;
            newScale.x = direction;
            _transform.localScale = newScale;
        }

        if (!_isPhaseTwo && health <= Mathf.CeilToInt(maxHealth * phaseTwoHealthPercent))
        {
            _isPhaseTwo = true;
            _animator.SetTrigger("phaseTwo");
        }

        if (!_currentState.checkValid(this))
        {
            float playerEnemyDistanceAbs = Math.Abs(_playerEnemyDistance);
            if (playerEnemyDistanceAbs > detectDistance)
            {
                _currentState = new Idle();
            }
            else if (!_isRollingOnCooldown)
            {
                _currentState = new Attack();
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

        if (_isRollingOnCooldown && !_isRolling)
        {
            rollAtkCooldown -= Time.deltaTime;
            if (rollAtkCooldown <= 0)
            {
                _isRollingOnCooldown = false;
                rollAtkCooldown = 10f;
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

        Vector2 newVelocity;
        newVelocity.x = 0;
        newVelocity.y = 0;
        _rigidbody.velocity = newVelocity;

        gameObject.layer = LayerMask.NameToLayer("Decoration");

        Vector2 newForce;
        newForce.x = _transform.localScale.x * deathForce.x;
        newForce.y = deathForce.y;
        _rigidbody.AddForce(newForce, ForceMode2D.Impulse);

        StartCoroutine(fadeCoroutine());
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

    IEnumerator rollToPlayer()
    {
        float moveDirection = Math.Abs(_playerEnemyDistance) < 0.1f ? 0 : Math.Sign(_playerEnemyDistance);

        Vector2 newVelocity = _rigidbody.velocity;
        newVelocity.x = moveDirection * walkSpeed * (_isPhaseTwo ? 1.25f : 1f) * 2f;
        _rigidbody.velocity = newVelocity;

        _animator.SetFloat("Speed", Math.Abs(newVelocity.x));

        if (Math.Abs(_playerEnemyDistance) < rollAtkRange)
        {
            _animator.SetTrigger("attackChaseEnd");
            stopMoving();
            yield return new WaitForSeconds(0.4f);
            _animator.SetBool("isRolling", false);
            yield return new WaitForSeconds(1f);
        }
        //yield return null;
    }

    public void attackPlayer()
    {
        if (_isAttackOnCooldown)
            return;

        StartCoroutine(attackRoutine());
    }

    private IEnumerator hurtCoroutine()
    {
        _isActionLocked = true;
        yield return new WaitForSeconds(hurtRecoilTime);

        _isActionLocked = false;
        stopMoving();
    }

    private IEnumerator attackRoutine()
    {
        _isAttackOnCooldown = true;
        _isActionLocked = true;

        if (!_isRollingOnCooldown && !_isRolling)
        {
            _isRolling = true;
            _animator.SetTrigger("attackChaseStart");
            yield return new WaitForSeconds(0.3f);
            _animator.SetBool("isRolling", true);

            

            yield return StartCoroutine(rollToPlayer());
            //_animator.SetBool("isRolling", false);
            _isRolling = false;
            _isRollingOnCooldown = true;
        }
        else
        {
            _animator.SetTrigger("attackMelee");
            yield return new WaitForSeconds(0.25f);

            /*
            bool hitPlayer = CheckHitResult();
            if (hitPlayer)
            {
                PlayerController playerController = _playerTransform.GetComponent<PlayerController>();
                playerController.hurt(damageToPlayer);
            }
            */

            yield return new WaitForSeconds(meleeCooldown);
        }

        _isActionLocked = false;
        _isAttackOnCooldown = false;
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

    public class Idle : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            return Math.Abs(enemyController.playerEnemyDistance()) > enemyController.detectDistance;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController bossController = (BossController)enemyController;
            bossController.stopMoving();
        }
    }

    public class Chase : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            BossController bossController = (BossController)enemyController;
            float playerEnemyDistanceAbs = Math.Abs(enemyController.playerEnemyDistance());
            return playerEnemyDistanceAbs <= enemyController.detectDistance &&
                   playerEnemyDistanceAbs > bossController.attackRange;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController bossController = (BossController)enemyController;
            bossController.moveToPlayer();
        }
    }

    public class Attack : State
    {
        public override bool checkValid(EnemyController enemyController)
        {
            BossController bossController = (BossController)enemyController;
            return Math.Abs(enemyController.playerEnemyDistance()) <= bossController.attackRange;
        }

        public override void Execute(EnemyController enemyController)
        {
            BossController bossController = (BossController)enemyController;
            if(bossController._isRollingOnCooldown) bossController.stopMoving();
            
            bossController.attackPlayer();
        }
    }

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
            return;
        }
        else return;
    }
    //gizmo hitbox draw
    private void OnDrawGizmosSelected()
    {
        if (attackPoint == null) return;

        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(attackPoint.position, attackBoxSize);
    }
}
