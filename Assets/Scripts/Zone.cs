﻿using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Collider))]
public class Zone : MonoBehaviour {

    public int numZone;

	void OnTriggerEnter(Collider other)
    {
        if (other.tag == "Player")
        {
            EnnemiSpawner.instance.SetCurrentZone(numZone);
        }
    }
}