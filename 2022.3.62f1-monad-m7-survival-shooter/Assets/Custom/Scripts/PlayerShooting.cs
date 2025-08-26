using Terresquall;
using UnityEngine;
public class PlayerShooting : MonoBehaviour
{
    [SerializeField] private float effectsDisplayTime = 0.2f;  // visible, but still encapsulated
    public int damagePerShot = 20;
    public float timeBetweenBullets = 0.15f;
    public float range = 100f;

    float timer;
    Ray shootRay = new Ray();
    RaycastHit shootHit;
    int shootableMask;
    ParticleSystem gunParticles;
    LineRenderer gunLine;
    AudioSource gunAudio;
    Light gunLight;

    public static bool TryPlay(Animator animator, string stateName, int layer = 0, float normalizedTime = 0f)
    {
        if (!animator) return false;

        // Try full path: "<LayerName>.<State>"
        string layerName = animator.GetLayerName(layer);
        int fullHash = Animator.StringToHash($"{layerName}.{stateName}");
        if (animator.HasState(layer, fullHash))
        {
            animator.Play(fullHash, layer, normalizedTime);
            return true;
        }

        // Fallback: short name only
        int shortHash = Animator.StringToHash(stateName);
        if (animator.HasState(layer, shortHash))
        {
            animator.Play(shortHash, layer, normalizedTime);
            return true;
        }

        Debug.LogWarning($"Animator state '{stateName}' not found on layer {layer} for {animator.gameObject.name}.");
        return false;
    }

    void Awake()
    {
        shootableMask = LayerMask.GetMask("Shootable");
        gunParticles = GetComponent<ParticleSystem>();
        gunLine = GetComponent<LineRenderer>();
        gunAudio = GetComponent<AudioSource>();
        gunLight = GetComponent<Light>();
    }


    void Update()
    {
        timer += Time.deltaTime;

        if (!GameSettingsManager.isVirtualJoystickEnabled)
        {
            if (Input.GetButton("Fire1") && timer >= timeBetweenBullets && Time.timeScale != 0)
            {
                Shoot();
            }
        }
        else
        {
            float h = VirtualJoystick.GetAxisRaw("Horizontal", 1);
            float v = VirtualJoystick.GetAxisRaw("Vertical", 1);

            if((v != 0 || h != 0) && (timer >= timeBetweenBullets && Time.timeScale != 0))
            {
                Shoot();
            }
        }


        if (timer >= timeBetweenBullets * (float)effectsDisplayTime)
        {
            DisableEffects();
        }
    }


    public void DisableEffects()
    {
        gunLine.enabled = false;
        gunLight.enabled = false;
    }


    void Shoot()
    {
        TryPlay(PlayerSpawner.PlayerAnimator, "Shoot");

        timer = 0f;

        gunAudio.Play();

        gunLight.enabled = true;

        gunParticles.Stop();
        gunParticles.Play();

        gunLine.enabled = true;
        gunLine.SetPosition(0, transform.position);

        shootRay.origin = transform.position;
        shootRay.direction = transform.forward;

        if (Physics.Raycast(shootRay, out shootHit, range, shootableMask))
        {
            EnemyHealth enemyHealth = shootHit.collider.GetComponent<EnemyHealth>();
            if (enemyHealth != null)
            {
                Debug.Log(damagePerShot);
                Debug.Log(shootHit.point);
                enemyHealth.TakeDamage(damagePerShot, shootHit.point);
            }
            gunLine.SetPosition(1, shootHit.point);
        }
        else
        {
            gunLine.SetPosition(1, shootRay.origin + shootRay.direction * range);
        }
    }
}
