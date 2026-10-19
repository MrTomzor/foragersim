using UnityEngine;

public class HumanControlManager : MonoBehaviour
{
    public bool humanControlEnabled = false;
    public string enableTopic = "/enable_human_control";

    public GameObject cameraObject;
    public Twist2DActuator velocityController;

    public void SetHumanControlEnabled(BoolMessage msg)
    {
        bool enbl = msg.data;
        humanControlEnabled = enbl;
        if (enbl)
        {
            cameraObject.SetActive(true);
            // Subscribe to the human control messages
        }
        else
        {
            cameraObject.SetActive(false);
            // Unsubscribe from the human control messages
        }
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        RoslikeTCPServer.GetInstance().RegisterTimerDiscrete(MainTimer, 1);
        RoslikeTCPServer.GetInstance().Subscribe<BoolMessage>(enableTopic, SetHumanControlEnabled);
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void MainTimer(TimerEvent ev)
    {
        if (humanControlEnabled)
        {
            ApplyHumanControls();
        }
    }

    public void ApplyHumanControls()
    {
        TwistMessage msg = new TwistMessage();
        msg.linear_x = Input.GetAxis("Vertical") * velocityController.maxLinearVelocity;
        msg.angular_z = - Input.GetAxis("Horizontal") * velocityController.maxAngularVelocity;

        velocityController.OnVelTwistMessage(msg);
    }
}
