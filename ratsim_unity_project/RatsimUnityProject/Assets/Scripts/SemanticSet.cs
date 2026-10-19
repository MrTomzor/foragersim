using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//add creation from editor menu
[CreateAssetMenu(fileName = "New Semantic Set", menuName = "Semantic Set", order = 1)]

public class SemanticSet : ScriptableObject
{
    public List<GameObject> prefabs;
}
