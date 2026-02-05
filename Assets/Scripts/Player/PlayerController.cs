using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

public class PlayerController : MonoBehaviour
{
    public int health;
    public float moveSpeed;
    public float jumpSpeed;
    public int jumpLeft;
    public Vector2 climbJumpForce;
    public float fallSpeed;
    public float sprintSpeed;
    public float sprintTime;
    public float sprintInterval;
    public float attackInterval;

    public Color invulnerableColor;
    public Vector2 hurtRecoil;
    public float hurtTime;
    public float hurtRecoverTime;
    public Vector2 deathRecoil;
    public float deathDelay;

    public Vector2 attackUpRecoil;
    public Vector2 attackForwardRecoil;
    public Vector2 attackDownRecoil;

    public GameObject attackUpEffect;
    public GameObject attackForwardEffect;
    public GameObject attackDownEffect;

    private bool _isGrounded;
    private bool _isClimb;
    private bool _isSprintable;
    private bool _isSprintReset;
    private bool _isInputEnabled;
    private bool _isFalling;
    private bool _isAttackable;

    private float _climbJumpDelay = 0.2f;
    private float _attackEffectLifeTime = 0.05f;

    private Animator _animator;
    private Rigidbody2D _rigidbody;
    private Transform _transform;
    private SpriteRenderer _spriteRenderer;
    private BoxCollider2D _boxCollider;




    [Header("HK-style Movement")]
    public float maxRunSpeed = 8f;
    public float groundAccel = 80f;
    public float groundDecel = 100f;
    public float airAccel = 60f;
    public float airDecel = 40f;

    private float _moveInput;
    private float _targetX;

    [Header("HK-style Jump")]
    public float fallGravityMultiplier = 2.5f;
    public float lowJumpGravityMultiplier = 2.0f; // when jump released early


    [Header("Wall Jump (Hold Extends Distance)")]
    public float wallJumpBaseX = 2.0f;      // tap: small horizontal push
    public float wallJumpMaxX = 7.5f;      // hold: can reach this
    public float wallJumpY = 12.0f;     // vertical launch

    public float wallJumpSustainTime = 0.12f;  // how long holding can extend X
    public float wallJumpSustainAccel = 80f;   // how fast X ramps up during sustain

    public float wallJumpControlLock = 0.08f;  // prevent immediate override by normal move code


    private bool _isWallJumping;
    private float _wallJumpSustainUntil;
    private float _wallJumpLockUntil;
    private int _wallDir; // +1 if wall on right, -1 if wall on left (push opposite)
    private bool isWallOnRight;




    // Start is called before the first frame update
    private void Start() {
        _isInputEnabled = true;
        _isSprintReset = true;
        _isAttackable = true;

        _animator = gameObject.GetComponent<Animator>();
        _rigidbody = gameObject.GetComponent<Rigidbody2D>();
        _transform = gameObject.GetComponent<Transform>();
        _spriteRenderer = gameObject.GetComponent<SpriteRenderer>();
        _boxCollider = gameObject.GetComponent<BoxCollider2D>();
    }

    // Update is called once per frame
    private void Update()
    {
        updatePlayerState();


        if (_isInputEnabled)
        {
            _moveInput = Input.GetAxisRaw("Horizontal"); // SNAPPY
            jumpControl();
            sprintControl();
            attackControl();
        }
    }

    private void FixedUpdate()
    {
        ApplyWallJumpSustain();

        // If you have normal horizontal movement, lock it briefly after wall jump
        if (Time.time < _wallJumpLockUntil) return;


        if (!_isInputEnabled) return;

        ApplyHorizontalMovement();
        ApplyBetterGravity();
    }

    private void ApplyWallJumpSustain()
    {
        if (!_isWallJumping) return;

        // stop sustaining after window
        if (Time.time > _wallJumpSustainUntil)
        {
            _isWallJumping = false;
            return;
        }

        // If player releases Jump early, stop extending distance immediately
        if (!Input.GetButton("Jump"))
        {
            _isWallJumping = false;
            return;
        }

        int pushDir = -_wallDir;
        float targetX = pushDir * wallJumpMaxX;

        float newX = Mathf.MoveTowards(
            _rigidbody.velocity.x,
            targetX,
            wallJumpSustainAccel * Time.fixedDeltaTime
        );

        _rigidbody.velocity = new Vector2(newX, _rigidbody.velocity.y);
    }


    private void ApplyHorizontalMovement()
    {
        _targetX = _moveInput * maxRunSpeed;

        float accel = _isGrounded
            ? (Mathf.Abs(_targetX) > 0.01f ? groundAccel : groundDecel)
            : (Mathf.Abs(_targetX) > 0.01f ? airAccel : airDecel);

        float newX = Mathf.MoveTowards(_rigidbody.velocity.x, _targetX, accel * Time.fixedDeltaTime);
        _rigidbody.velocity = new Vector2(newX, _rigidbody.velocity.y);

        // Your facing + animations can still use _moveInput
        if (!_isClimb)
        {
            if (_moveInput != 0)
            {
                // flip logic
                Vector3 s = transform.localScale;
                s.x = (_moveInput < 0) ? 1 : -1; // you have inverted sprite
                s.y = 1; s.z = 1;
                transform.localScale = s;

                _animator.SetBool("IsRun", true);
            }
            else
            {
                _animator.SetBool("IsRun", false);
            }
        }
    }

    private void ApplyBetterGravity()
    {
        if (_isClimb) return; // keep your climb behavior

        if (_rigidbody.velocity.y < 0)
        {
            // falling -> faster fall
            _rigidbody.velocity += Vector2.up * Physics2D.gravity.y * (fallGravityMultiplier - 1f) * Time.fixedDeltaTime;
        }
        else if (_rigidbody.velocity.y > 0 && !Input.GetButton("Jump"))
        {
            // jump released early -> cut jump short
            _rigidbody.velocity += Vector2.up * Physics2D.gravity.y * (lowJumpGravityMultiplier - 1f) * Time.fixedDeltaTime;
        }
    }



    private void OnCollisionEnter2D(Collision2D collision)
    {
        // enter climb state
        if (collision.collider.tag == "Wall" && !_isGrounded)
        {
            _rigidbody.gravityScale = 0;

            Vector2 newVelocity;
            newVelocity.x = 0;
            newVelocity.y = -2;

            _rigidbody.velocity = newVelocity;

            _isClimb = true;
            _animator.SetBool("IsClimb", true);

            _isSprintable = true;

            if(transform.localScale.x == -1) isWallOnRight = true;
            else isWallOnRight = false;
        }
    }

    private void OnCollisionStay2D(Collision2D collision)
    {
        if (collision.collider.tag == "Wall" && _isFalling && !_isClimb)
        {
            OnCollisionEnter2D(collision);
        }
    }

    public void hurt(int damage)
    {
        gameObject.layer = LayerMask.NameToLayer("PlayerInvulnerable");

        health = Math.Max(health - damage, 0);

        if (health == 0)
        {
            die();
            return;
        }

        // enter invulnerable state
        _animator.SetTrigger("IsHurt");

        // stop player movement
        Vector2 newVelocity;
        newVelocity.x = 0;
        newVelocity.y = 0;
        _rigidbody.velocity = newVelocity;

        // visual effect
        _spriteRenderer.color = invulnerableColor;

        // death recoil
        Vector2 newForce;
        newForce.x = -_transform.localScale.x * hurtRecoil.x;
        newForce.y = hurtRecoil.y;
        _rigidbody.AddForce(newForce, ForceMode2D.Impulse);

        _isInputEnabled = false;

        StartCoroutine(recoverFromHurtCoroutine());
    }

    private IEnumerator recoverFromHurtCoroutine()
    {
        yield return new WaitForSeconds(hurtTime);
        _isInputEnabled = true;
        yield return new WaitForSeconds(hurtRecoverTime);
        _spriteRenderer.color = Color.white;
        gameObject.layer = LayerMask.NameToLayer("Player");
    }

    private void OnCollisionExit2D(Collision2D collision)
    {
        // exit climb state
        if (collision.collider.tag == "Wall")
        {
            _isClimb = false;
            _animator.SetBool("IsClimb", false);

            _rigidbody.gravityScale = 1;
        }
    }

    /* ######################################################### */

    private void updatePlayerState()
    {
        _isGrounded = checkGrounded();
        _animator.SetBool("IsGround", _isGrounded);

        float verticalVelocity = _rigidbody.velocity.y;
        _animator.SetBool("IsDown", verticalVelocity < 0);

        if (_isGrounded && verticalVelocity == 0)
        {
            _animator.SetBool("IsJump", false);
            _animator.ResetTrigger("IsJumpFirst");
            _animator.ResetTrigger("IsJumpSecond");
            _animator.SetBool("IsDown", false);

            jumpLeft = 2;
            _isClimb = false;
            _isSprintable = true;
        }
        else if(_isClimb)
        {
            // one remaining jump chance after climbing
            jumpLeft = 1;
        }
    }

    private void move()
    {
        // calculate movement
        float horizontalMovement = Input.GetAxis("Horizontal") * moveSpeed;

        // set velocity
        Vector2 newVelocity;
        newVelocity.x = horizontalMovement;
        newVelocity.y = _rigidbody.velocity.y;
        _rigidbody.velocity = newVelocity;

        if (!_isClimb)
        {
            // the sprite itself is inversed 
            float moveDirection = -transform.localScale.x * horizontalMovement;

            if (moveDirection < 0)
            {
                // flip player sprite
                Vector3 newScale;
                newScale.x = horizontalMovement < 0 ? 1 : -1;
                newScale.y = 1;
                newScale.z = 1;

                transform.localScale = newScale;

                if (_isGrounded)
                {
                    // turn back animation
                    _animator.SetTrigger("IsRotate");
                }
            }
            else if (moveDirection > 0)
            {
                // move forward
                _animator.SetBool("IsRun", true);
            }
        }

        // stop
        if (Input.GetAxis("Horizontal") == 0)
        {
            _animator.SetTrigger("stopTrigger");
            _animator.ResetTrigger("IsRotate");
            _animator.SetBool("IsRun", false);
        }
        else
        {
            _animator.ResetTrigger("stopTrigger");
        }
    }

    private void jumpControl()
    {
        if (!Input.GetButtonDown("Jump"))
            return;

        if (_isClimb)
            climbJump();
        else if (jumpLeft > 0)
            jump();
    }

    private void sprintControl()
    {
        if (Input.GetKeyDown(KeyCode.K) && _isSprintable && _isSprintReset)
            sprint();
    }

    private void attackControl()
    {
        if (Input.GetKeyDown(KeyCode.J) && !_isClimb && _isAttackable)
            attack();
    }

    private void die()
    {
        _animator.SetTrigger("IsDead");

        _isInputEnabled = false;

        // stop player movement
        Vector2 newVelocity;
        newVelocity.x = 0;
        newVelocity.y = 0;
        _rigidbody.velocity = newVelocity;

        // visual effect
        _spriteRenderer.color = invulnerableColor;

        // death recoil
        Vector2 newForce;
        newForce.x = -_transform.localScale.x * deathRecoil.x;
        newForce.y = deathRecoil.y;
        _rigidbody.AddForce(newForce, ForceMode2D.Impulse);

        StartCoroutine(deathCoroutine());
    }

    private IEnumerator deathCoroutine()
    {
        var material = _boxCollider.sharedMaterial;
        material.bounciness = 0.3f;
        material.friction = 0.3f;
        // unity bug, need to disable and then enable to make it work
        _boxCollider.enabled = false;
        _boxCollider.enabled = true;

        yield return new WaitForSeconds(deathDelay);

        material.bounciness = 0;
        material.friction = 0;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    /* ######################################################### */

    private bool checkGrounded()
    {
        Vector2 origin = _transform.position;

        float radius = 0.2f;

        // detect downwards
        Vector2 direction;
        direction.x = 0;
        direction.y = -1;

        float distance = 0.5f;
        LayerMask layerMask = LayerMask.GetMask("Platform");

        RaycastHit2D hitRec = Physics2D.CircleCast(origin, radius, direction, distance, layerMask);
        return hitRec.collider != null;
    }

    private void jump()
    {
        Vector2 newVelocity;
        newVelocity.x = _rigidbody.velocity.x;
        newVelocity.y = jumpSpeed;

        _rigidbody.velocity = newVelocity;

        _animator.SetBool("IsJump", true);
        jumpLeft -= 1;
        if (jumpLeft == 0)
        {
            _animator.SetTrigger("IsJumpSecond");
        } 
        else if (jumpLeft == 1)
        {
            _animator.SetTrigger("IsJumpFirst");
        }
    }

    private void climbJump()
    {
        if (_isClimb && Input.GetButtonDown("Jump"))
        {
            DoWallJumpImmediate();
        }

    }

    private void DoWallJumpImmediate()
    {
        _wallDir = GetWallDir();       // +1 wall right, -1 wall left
        int pushDir = -_wallDir;       // push away from wall

        // Instant jump begins NOW:
        _rigidbody.velocity = new Vector2(pushDir * wallJumpBaseX, wallJumpY);

        _isWallJumping = true;
        _wallJumpSustainUntil = Time.time + wallJumpSustainTime;
        _wallJumpLockUntil = Time.time + wallJumpControlLock;

        _isClimb = false; // exit climbing state if you have it
    }


    private int GetWallDir()
    {
        // Return +1 if wall is on right, -1 if wall is on left
        // Replace with your actual wall check booleans.
        if (isWallOnRight) return +1;
        return -1;
    }



    private IEnumerator climbJumpCoroutine(float delay)
    {
        yield return new WaitForSeconds(delay);

        _isInputEnabled = true;

        _animator.ResetTrigger("IsClimbJump");

        // jump to the opposite direction
        Vector3 newScale;
        newScale.x = -transform.localScale.x;
        newScale.y = 1;
        newScale.z = 1;

        transform.localScale = newScale;
    }

    private void sprint()
    {
        // reject input during sprinting
        _isInputEnabled = false;
        _isSprintable = false;
        _isSprintReset = false;

        Vector2 newVelocity;
        newVelocity.x = transform.localScale.x * (_isClimb ? sprintSpeed : -sprintSpeed);
        newVelocity.y = 0;

        _rigidbody.velocity = newVelocity;

        if (_isClimb)
        {
            // sprint to the opposite direction
            Vector3 newScale;
            newScale.x = -transform.localScale.x;
            newScale.y = 1;
            newScale.z = 1;

            transform.localScale = newScale;
        }

        _animator.SetTrigger("IsSprint");
        StartCoroutine(sprintCoroutine(sprintTime, sprintInterval));
    }

    private IEnumerator sprintCoroutine(float sprintDelay, float sprintInterval)
    {
        yield return new WaitForSeconds(sprintDelay);
        _isInputEnabled = true;
        _isSprintable = true;

        yield return new WaitForSeconds(sprintInterval);
        _isSprintReset = true;
    }

    private void attack()
    {
        float verticalDirection = Input.GetAxis("Vertical");
        if (verticalDirection > 0)
            attackUp();
        else if (verticalDirection < 0 && !_isGrounded)
            attackDown();
        else
            attackForward();
    }

    private void attackUp()
    {
        _animator.SetTrigger("IsAttackUp");
        attackUpEffect.SetActive(true);

        Vector2 detectDirection;
        detectDirection.x = 0;
        detectDirection.y = 1;

        StartCoroutine(attackCoroutine(attackUpEffect, _attackEffectLifeTime, attackInterval, detectDirection, attackUpRecoil));
    }

    private void attackForward()
    {
        _animator.SetTrigger("IsAttack");
        attackForwardEffect.SetActive(true);

        Vector2 detectDirection;
        detectDirection.x = -transform.localScale.x;
        detectDirection.y = 0;

        Vector2 recoil;
        recoil.x = transform.localScale.x > 0 ? -attackForwardRecoil.x : attackForwardRecoil.x;
        recoil.y = attackForwardRecoil.y;

        StartCoroutine(attackCoroutine(attackForwardEffect, _attackEffectLifeTime, attackInterval, detectDirection, recoil));
    }

    private void attackDown()
    {
        _animator.SetTrigger("IsAttackDown");
        attackDownEffect.SetActive(true);

        Vector2 detectDirection;
        detectDirection.x = 0;
        detectDirection.y = -1;

        StartCoroutine(attackCoroutine(attackDownEffect, _attackEffectLifeTime, attackInterval, detectDirection, attackDownRecoil));
    }

    private IEnumerator attackCoroutine(GameObject attackEffect,float effectDelay, float attackInterval, Vector2 detectDirection, Vector2 attackRecoil)
    {
        Vector2 origin = _transform.position;

        float radius = 0.6f;

        float distance = 1.5f;
        LayerMask layerMask = LayerMask.GetMask("Enemy") | LayerMask.GetMask("Trap") | LayerMask.GetMask("Switch") | LayerMask.GetMask("Projectile");

        RaycastHit2D[] hitRecList = Physics2D.CircleCastAll(origin, radius, detectDirection, distance, layerMask);

        foreach (RaycastHit2D hitRec in hitRecList)
        {
            GameObject obj = hitRec.collider.gameObject;

            string layerName = LayerMask.LayerToName(obj.layer);
            
            if (layerName == "Switch")
            {
                Switch swithComponent = obj.GetComponent<Switch>();
                if (swithComponent != null)
                    swithComponent.turnOn();
            } 
            else if (layerName == "Enemy")
            {
                EnemyController enemyController = obj.GetComponent<EnemyController>();
                if (enemyController != null)
                    enemyController.hurt(1);
            }
            else if (layerName == "Projectile")
            {
                Destroy(obj);
            }
        }

        if (hitRecList.Length > 0)
        {
            _rigidbody.velocity = attackRecoil;
        }

        yield return new WaitForSeconds(effectDelay);

        attackEffect.SetActive(false);

        // attack cool down
        _isAttackable = false;
        yield return new WaitForSeconds(attackInterval);
        _isAttackable = true;
    }
}
