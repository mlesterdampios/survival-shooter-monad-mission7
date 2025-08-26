using UnityEngine;
using System.Collections;
public class EnemyAttack : MonoBehaviour
{
    public float timeBetweenAttacks = 0.5f;
    public int attackDamage = 10;


    Animator anim;
    GameObject player;
    EnemyHealth enemyHealth;
    bool playerInRange;
    float timer;

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
        player = GameObject.FindGameObjectWithTag("Player");
        enemyHealth = GetComponent<EnemyHealth>();
        anim = GetComponent<Animator>();
        if(anim == null)
            anim = GetComponentInChildren<Animator>();
    }


    void OnTriggerEnter(Collider other)
    {
        if (other.gameObject == player)
        {
            playerInRange = true;
        }
    }


    void OnTriggerExit(Collider other)
    {
        if (other.gameObject == player)
        {
            playerInRange = false;
        }
    }


    void Update()
    {
        timer += Time.deltaTime;

        if (timer >= timeBetweenAttacks && playerInRange && enemyHealth.CurrentHealth > 0)
        {
            Attack();
        }

        if (PlayerSpawner._PlayerHealth.CurrentHealth <= 0)
        {
            anim.SetTrigger("PlayerDead");
        }
    }


    void Attack()
    {
        timer = 0f;

        if (PlayerSpawner._PlayerHealth.CurrentHealth > 0)
        {
            TryPlay(anim, "Attack");
            PlayerSpawner._PlayerHealth.TakeDamage(attackDamage);
        }
    }
}
