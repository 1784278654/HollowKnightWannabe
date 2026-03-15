using UnityEngine;

/// <summary>
/// Attach to the ash projectile prefab.
/// Homes weakly toward the player over its lifetime.
/// </summary>
public class AshProjectile : MonoBehaviour
{
    [SerializeField] private float speed        = 8f;
    [SerializeField] private float homingStrength = 60f;  // degrees/sec turn rate
    [SerializeField] private float lifetime     = 4f;

    private int     _damage;
    private Vector2 _direction;
    private float   _timer;
    private Transform _playerTransform;

    public void Init(Vector2 direction, int damage)
    {
        _direction        = direction.normalized;
        _damage           = damage;
        _timer            = lifetime;
        _playerTransform  = GlobalController.Instance.player.GetComponent<Transform>();

        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
    }

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer <= 0f) { Destroy(gameObject); return; }

        // Weak homing — rotate _direction toward player
        if (_playerTransform != null)
        {
            Vector2 toPlayer = ((Vector2)_playerTransform.position - (Vector2)transform.position).normalized;
            _direction = Vector2.MoveTowards(
                _direction,
                toPlayer,
                homingStrength * Mathf.Deg2Rad * Time.deltaTime
            ).normalized;
        }

        transform.Translate(_direction * speed * Time.deltaTime, Space.World);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        PlayerController pc = other.GetComponent<PlayerController>();
        if (pc != null)
        {
            pc.hurt(_damage);
            Destroy(gameObject);
        }
        // Destroy on terrain too
        else if (other.gameObject.layer == LayerMask.NameToLayer("Ground"))
        {
            Destroy(gameObject);
        }
    }
}
