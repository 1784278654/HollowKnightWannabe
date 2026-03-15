using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class Menu : MonoBehaviour
{
    private void Start()
    {
        SoundManager.Instance.PlayBGM("bgm");
        SoundManager.Instance.SetBGMVolume(1f);
    }
    public void clickStartButton()
    {
        SoundManager.Instance.PlaySFX("click");
        PlayerPrefs.SetString("Milestone", "Spawn");
        clickLoadButton();
    }

    public void clickLoadButton()
    {
        SoundManager.Instance.PlaySFX("click");
        SceneManager.LoadScene(PlayerPrefs.GetString("Milestone"));
    }

    public void clickQuitButton()
    {
        SoundManager.Instance.PlaySFX("click");
        Application.Quit();
    }
}
