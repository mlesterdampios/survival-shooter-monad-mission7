using UnityEngine;

public class GameOverManager : MonoBehaviour
{
    public float restartDelay = 5f;

    Animator anim;
    float restartTimer;

    bool hasLoadedLeaderboard = false;

    void Awake()
    {
        anim = GetComponent<Animator>();
    }


    void Update()
    {
        if (PlayerSpawner._PlayerHealth.CurrentHealth <= 0)
        {
            anim.SetTrigger("GameOver");

            restartTimer += Time.deltaTime;

            if(restartTimer >= restartDelay)
            {
                Application.LoadLevel(Application.loadedLevel);
            }

            if (!hasLoadedLeaderboard)
            {
                hasLoadedLeaderboard = true;

                LeaderboardManager.FetchAndPopulateDefault();
            }
        }
    }
}
