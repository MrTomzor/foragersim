using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class Twist2DActuator : MonoBehaviour
{
    RoslikeTCPServer conn;
    public float maxLinearVelocity = 10f;
    public float maxAngularVelocity = 5f;

    public float maxLinearAcceleration = 5f;
    public float maxAngularAcceleration = 2f;

    public string velCmdTopic = "/cmd_vel";
    public string accelCmdTopic = "/cmd_accel";

    public float accelModeLinearDrag = 0.2f;
    public float accelModeAngularDrag = 0.2f;
    public bool verbose = false;

    // --- Faults ---
    // Added to the commanded angular velocity (ROS frame: +z = CCW = left turn)
    // whenever |linear_x| > 0. Units: rad/s.
    [Header("Faults")]
    public float steeringBias = 0f;
    // If true, clamps away commanded turns in that direction (ROS frame: +angular_z = left).
    public bool blockLeftTurn = false;
    public bool blockRightTurn = false;

    // --- Human control ---
    [Header("Human Control")]
    public string humanControlTopic = "/enable_human_control";
    public bool humanControlEnabled = false;

    /// <summary>
    /// Whether human input uses velocity (direct) or acceleration mode.
    /// </summary>
    public enum HumanControlMode { Velocity, Acceleration }
    public HumanControlMode humanControlMode = HumanControlMode.Velocity;

    [Tooltip("Speed multiplier applied while the Sprint action (LeftShift) IS held. " +
             "Full speed is the default; holding Shift walks slowly (Minecraft-style sneak).")]
    [Range(0f, 1f)]
    [FormerlySerializedAs("humanControlDefaultSpeedScale")]
    public float humanControlSlowSpeedScale = 0.5f;

    [Tooltip("Sim time only advances on steps where the player moves (WASD) or holds the " +
             "wait key (Space). With no input the step is held, so reaction time and gaps " +
             "between keypresses don't cost the player episode steps.")]
    public bool humanControlWaitForInput = true;

    private InputSystem_Actions _inputActions;
    private Vector2 _moveInput;

    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.Subscribe<TwistMessage>(velCmdTopic, OnVelTwistMessage);
        conn.Subscribe<TwistMessage>(accelCmdTopic, OnAccelTwistMessage);
        conn.Subscribe<BoolMessage>(humanControlTopic, OnHumanControlToggle);
        conn.RegisterPreStepHook(OnPreStep);

        // Set up Input System actions
        _inputActions = new InputSystem_Actions();
    }

    void OnDestroy()
    {
        _inputActions?.Disable();
    }

    // --- TCP message handlers ---

    public void OnVelTwistMessage(TwistMessage msg)
    {
        if (humanControlEnabled) return; // ignore TCP commands during human control

        if (verbose)
            Debug.Log($"Received TwistMessage: linear_x={msg.linear_x}, linear_y={msg.linear_y}, angular_z={msg.angular_z}");
        ApplyVelocity(msg);
    }

    public void OnAccelTwistMessage(TwistMessage msg)
    {
        if (humanControlEnabled) return;

        if (verbose)
            Debug.Log($"Received AccelTwistMessage: linear_x={msg.linear_x}, linear_y={msg.linear_y}, angular_z={msg.angular_z}");
        ApplyAcceleration(msg);
    }

    // --- Shared physics application ---

    private float ApplyAngularFaults(float angularZ, float linearX)
    {
        if (Mathf.Abs(linearX) > 1e-6f) angularZ += steeringBias;
        if (blockLeftTurn && angularZ > 0f) angularZ = 0f;
        if (blockRightTurn && angularZ < 0f) angularZ = 0f;
        return angularZ;
    }

    private void ApplyVelocity(TwistMessage msg)
    {
        float angularZ = ApplyAngularFaults(msg.angular_z, msg.linear_x);
        Vector3 forward = transform.forward * msg.linear_x;
        Vector3 left = -transform.right * msg.linear_y;
        Vector3 rotationRad = new Vector3(0, -angularZ, 0);

        var rb = GetComponent<Rigidbody>();
        rb.linearVelocity = forward + left;
        rb.angularVelocity = rotationRad;
    }

    private void ApplyAcceleration(TwistMessage msg)
    {
        float angularZ = ApplyAngularFaults(msg.angular_z, msg.linear_x);
        Vector3 forward = transform.forward * msg.linear_x;
        Vector3 left = -transform.right * msg.linear_y;
        Vector3 rotationRad = new Vector3(0, -angularZ, 0);

        var rb = GetComponent<Rigidbody>();

        float dt = conn.physicsStepTime;
        rb.linearVelocity += (forward + left) * dt;
        rb.angularVelocity += rotationRad * dt;

        // Apply drag to simulate acceleration control
        rb.linearVelocity *= (1.0f - accelModeLinearDrag);
        rb.angularVelocity *= (1.0f - accelModeAngularDrag);
    }

    // --- Human control ---

    private void OnHumanControlToggle(BoolMessage msg)
    {
        humanControlEnabled = msg.data;
        if (humanControlEnabled)
        {
            _inputActions.Enable();
            Debug.Log("Twist2DActuator: human control ENABLED");
        }
        else
        {
            _inputActions.Disable();
            // Stop the agent when switching back to TCP control
            var rb = GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Debug.Log("Twist2DActuator: human control DISABLED");
        }
    }

    // Runs before Physics.Simulate, so this step's input drives this step's motion.
    // Returns false to hold the step (wait-for-input mode, no key pressed).
    private bool OnPreStep()
    {
        if (!humanControlEnabled) return true;

        _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
        // Jump is bound to Space: stand still, but let time pass.
        bool waiting = _inputActions.Player.Jump.IsPressed();
        bool moving = _moveInput.sqrMagnitude > 1e-6f;

        if (humanControlWaitForInput && !moving && !waiting) return false;
        if (waiting) _moveInput = Vector2.zero;

        // Full speed by default; holding Sprint (LeftShift) slows down.
        bool slow = _inputActions.Player.Sprint.IsPressed();
        float scale = slow ? humanControlSlowSpeedScale : 1f;

        TwistMessage msg = new TwistMessage();
        // Move.y = forward/backward (W/S or left stick Y), Move.x = left/right (A/D or left stick X)
        if (humanControlMode == HumanControlMode.Velocity)
        {
            msg.linear_x = _moveInput.y * maxLinearVelocity * scale;
            msg.angular_z = -_moveInput.x * maxAngularVelocity * scale;
            if (verbose)
                Debug.Log("Human control raw input: " + _moveInput + " slow=" + slow + " wait=" + waiting + " -> velocity cmd: linear_x=" + msg.linear_x + ", angular_z=" + msg.angular_z);
            ApplyVelocity(msg);
        }
        else
        {
            msg.linear_x = _moveInput.y * maxLinearAcceleration * scale;
            msg.angular_z = -_moveInput.x * maxAngularAcceleration * scale;
            ApplyAcceleration(msg);
        }
        return true;
    }

    void Update()
    {
    }
}
