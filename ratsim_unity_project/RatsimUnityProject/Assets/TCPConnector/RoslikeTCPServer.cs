using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;
using Newtonsoft.Json; // Add Newtonsoft.Json via Unity Package Manager or .dll
using UnityEngine.SceneManagement;
using System.Linq;



public class RoslikeTCPServer : MonoBehaviour
{
    public bool verbose = false; // Enable verbose logging
    public bool timingVerbose = false;
    public float physicsStepTime = 0.02f; // 50Hz

    static RoslikeTCPServer instance;
    public static RoslikeTCPServer GetInstance()
    {
        return instance;
    }

    // SCENE HANDLING

    private string currentLoadedScene = null;

    void OnSceneSelectReceived(StringMessage msg)
    {
        string sceneName = msg.data;
        
        if (verbose)
        {
            Debug.Log($"Received scene load request: {sceneName}");
        }

        
        if (Application.CanStreamedLevelBeLoaded(sceneName))
        {
            // Unload previous scene if exists
            /*if (currentLoadedScene != null && SceneManager.GetSceneByName(currentLoadedScene).isLoaded)
            {
                SceneManager.UnloadSceneAsync(currentLoadedScene);
                Debug.Log($"Unloading previous scene: {currentLoadedScene}");
            }*/
            
            // Load new scene
            SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
            currentLoadedScene = sceneName;
            
            Debug.Log($"Loading scene: {sceneName}");

        }
        else
        {
            Debug.LogWarning($"Scene '{sceneName}' not found in build settings!");
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CleanupDestroyedTimersAndSubscribers();
        Debug.Log($"Scene loaded: {scene.name}, cleaned up timers/subscribers");
    }

    private TcpListener listener;
    private Thread serverThread;
    private StreamWriter writer;

    // Change the subscribers dictionary to support different message types per topic
    private Dictionary<string, List<RoslikeSubscriber>> subscribersByTopic = new();
    private List<RoslikeTimer> timers = new List<RoslikeTimer>();
    // Run on the main thread after subscriber dispatch and before Physics.Simulate, on
    // physics steps only. A hook returning false holds the whole step: no physics, no
    // timers, and "/sim_control/step_skipped" is published so the client knows sim time
    // did not advance. Used by human control's wait-for-input mode.
    private List<Func<bool>> preStepHooks = new List<Func<bool>>();

    private List<Tuple<string, Message>> receivedMessages = new List<Tuple<string, Message>>();

    List<Tuple<string, Message>> DeserializeMessages(Newtonsoft.Json.Linq.JArray rawMsgs)
    {
        var messages = new List<Tuple<string, Message>>();
        foreach (var rawMsg in rawMsgs)
        {
            // Deserialize outer wrapper
            var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(rawMsg.ToString());
            string typeName = wrapper["type"].ToString();
            string topic = wrapper["topic"].ToString();
            var dataJson = wrapper["data"].ToString();

            if (verbose)
            {
                Debug.Log($"Received message of type {typeName} on topic {topic}");
            }

            //if (messageTypeRegistry.TryGetValue(typeName, out Type msgType))
                Type msgType = MessageRegistry.GetMessageType(typeName);
            if (msgType != null)
            {
                var fullJson = $"{{\"topic\":\"{topic}\",\"type\":\"{typeName}\",\"{nameof(dataJson)}\":{dataJson}}}";
                if (verbose)
                {
                    Debug.Log($"Deserializing message: {fullJson}");
                }
                try{
                    var msg = (Message)JsonConvert.DeserializeObject(dataJson, msgType);
                    messages.Add(new Tuple<string, Message>(topic, msg));
                }
                catch(Exception e)
                {
                    Debug.LogError($"Failed to deserialize message of type {typeName} on topic {topic}: {e.Message}");
                }
                //var msg = (Message)JsonConvert.DeserializeObject(dataJson, msgType);

                //messages.Add(new Tuple<string, Message>(topic, msg));
            }
            else
            {
                Debug.LogError($"Unknown message type: {typeName}");
            }
        }
        return messages;
    }

    void DispatchReceivedMessage(string topic, Message msg)
    {
        //Debug.Log("Dispatching message to topic: " + topic);
        //Debug.Log("Message type: " + msg.GetType().Name);
        //Debug.Log("Message data: " + JsonConvert.SerializeObject(msg));

        if(!subscribersByTopic.ContainsKey(topic))
        {
            // No subscribers for this topic
            Debug.LogWarning($"No subscribers for topic receiving msg: {topic}");
            return;
        }

        foreach (var subscriber in subscribersByTopic[topic])
        {
            subscriber.callback(msg);
        }
    }

    public void CleanupDestroyedTimersAndSubscribers()
    {
        var numTimersBefore = timers.Count;
        var numSubscribersBefore = subscribersByTopic.Values.Sum(list => list.Count);

        // ---- Clean timers ----
        timers.RemoveAll(timer =>
        {
            if (timer.callback == null) return true; // safety
            if (timer.callback.Target is UnityEngine.Object unityObj)
            {
                return unityObj == null; // destroyed
            }
            return false; // keep non-Unity objects
        });

        var numTimersAfter = timers.Count;

        // ---- Clean pre-step hooks ----
        preStepHooks.RemoveAll(hook => hook.Target is UnityEngine.Object unityObj && unityObj == null);

        // ---- Clean subscribers ----
        foreach (var topic in subscribersByTopic.Keys.ToList()) // ToList to avoid modifying collection while iterating
        {
            var subs = subscribersByTopic[topic];
            subs.RemoveAll(sub =>
            {
                if (sub == null) return true; // safety
                if (sub.owner is UnityEngine.Object unityObj)
                {
                    return unityObj == null; // destroyed
                }
                return false; // keep non-Unity objects
            });

            // Optional: remove the topic entry if no subscribers remain
            if (subs.Count == 0)
            {
                subscribersByTopic.Remove(topic);
            }
        }
        var numSubscribersAfter = subscribersByTopic.Values.Sum(list => list.Count);

        
        Debug.Log($"Cleanup complete. Remaining timers: {timers.Count} (removed {numTimersBefore - numTimersAfter}), remaining topics: {subscribersByTopic.Count} (removed {numSubscribersBefore - numSubscribersAfter})");
        // print their names
        


        Debug.Log("Remaining subscribers by topic:");
        foreach (var topic in subscribersByTopic.Keys)
        {
            Debug.Log($"Topic: {topic}, Subscribers: {subscribersByTopic[topic].Count}");
        }
    }

    public void RegisterTimerDiscrete( Action<TimerEvent> callback, uint stepsPerTick)
    {
        var timer = new RoslikeTimer(callback, true, stepsPerTick);
        timers.Add(timer);
    }

    public void RegisterPreStepHook(Func<bool> hook)
    {
        preStepHooks.Add(hook);
    }

    bool RunPreStepHooks()
    {
        // Every hook runs (no short-circuit): hooks may apply state for this step.
        bool advance = true;
        foreach (var hook in preStepHooks)
        {
            if (hook.Target is UnityEngine.Object unityObj && unityObj == null) continue; // destroyed
            advance &= hook();
        }
        return advance;
    }

    public void RegisterTimerContinuous(Action<TimerEvent> callback, float periodSeconds)
    {
        var timer = new RoslikeTimer(callback, false, periodSeconds);
        timers.Add(timer);
    }

    void HandleTimers(uint elapsedPhysicsSteps, float elapsedSeconds)
    {
        foreach (var timer in timers)
        {
            timer.HandleSteps(elapsedPhysicsSteps, elapsedSeconds);
        }
    }

    public void Subscribe<T>(string topic, Action<T> callback) where T : Message
    {
        if (!subscribersByTopic.TryGetValue(topic, out var list))
        {
            list = new List<RoslikeSubscriber>();
            subscribersByTopic[topic] = list;
        }

        UnityEngine.Object owner = callback.Target as UnityEngine.Object;

        list.Add(new RoslikeSubscriber
        {
            owner = owner,
            callback = (Message msg) =>
            {
                if (msg is T typed)
                    callback(typed);
            }
        });
    }

    public void Publish(string topic, Message msg)
    {

        // Serialize the message
        MessageEnvelope wrapper = new MessageEnvelope
        (
            topic: topic,
            type: msg.GetType().Name,
            data: msg
        );

        // Add to envelopes to publish, since publishing is done synchronously in the main thread
        envelopesToPublish.Add(wrapper);
    }

    void SendAndClearEnvelopes()
    {
        string replyJson = JsonConvert.SerializeObject(new { messages = envelopesToPublish }) + "\n";
        writer.Write(replyJson);
        writer.Flush();
                                
        // Clear envelopes for next step
        envelopesToPublish.Clear();
    }

    void StepRequestCallback(StepRequestMessage msg)
    {
        if (msg.physicsEnabled != physicsEnabled)
        {
            Debug.LogWarning($"Received StepMessage with physicsEnabled={msg.physicsEnabled}, switching!");
        }
        physicsEnabled = msg.physicsEnabled;
    }

    List<MessageEnvelope> envelopesToPublish = new List<MessageEnvelope>();

    void Start()
    {
        if (instance != null)
        {
            Debug.LogWarning("Multiple instances of TcpServer detected, destroying this one.");
            Destroy(this);
            return;
        }
        instance = this;

        Physics.autoSyncTransforms = false;  // Add this - prevents auto syncing
        // Force deterministic physics
        Time.fixedDeltaTime = physicsStepTime;
        Time.maximumDeltaTime = physicsStepTime;

        // Track current scene
        currentLoadedScene = SceneManager.GetActiveScene().name;
        // This keeps it alive across scene loads
        DontDestroyOnLoad(gameObject);

        // This removes destroyed timers and subscribers on scene load
        SceneManager.sceneLoaded += OnSceneLoaded;
        
        
        Physics.simulationMode = SimulationMode.Script;
        Application.targetFrameRate = 10000;

        // Subscribe to scene select topic
        Subscribe<StringMessage>("/sim_control/scene_select", OnSceneSelectReceived);

        // Setup core subscribers
        Subscribe<StepRequestMessage>("/sim_control/do_step", StepRequestCallback);

        serverThread = new Thread(MainLoop);
        serverThread.IsBackground = true;
        serverThread.Start();

    }


    public volatile bool stepRequested = false;
    public bool physicsEnabled = true;
    public uint stepIndex { get; private set; } = 0;
    public uint physicsStepIndex { get; private set; } = 0;


    void Update()
    {
        if (stepRequested)
        {
            // Handle Subscriber calbacks
            foreach (var msg in receivedMessages)
            {
                DispatchReceivedMessage(msg.Item1, msg.Item2);
            }

            if (physicsEnabled && !RunPreStepHooks())
            {
                Publish("/sim_control/step_skipped", new BoolMessage { data = true });
                stepRequested = false;
                return;
            }

            // Step Unity physics
            if (physicsEnabled)
            {
                //Debug.Log("Stepping physics simulation...");
                physicsStepIndex++;
                Physics.Simulate(physicsStepTime);
            }

            // Handle Publishers and Timers
            HandleTimers(1, physicsStepTime);
            
            // Signify end of all mainthread operations
            stepRequested = false;
        }
    }


    void HandleClient(TcpClient client)
    {
        // ASSUME 1 CLIENT AT A TIME ALWAYS
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

        //try
        {
            while (client.Connected)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                //var mainloopstart = Time.realtimeSinceStartup;
                // Read incoming messages
                var line = reader.ReadLine();
                if (line == null) break; // Client disconnected
                var readingDoneTime = stopwatch.Elapsed.TotalSeconds;

                //Debug.Log("Received data len: " + line.Length);

                // try to parse the outer wrapper
                //var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                Dictionary<string, object> wrapper = null;
                try
                {
                    wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("Failed to parse message wrapper: " + e.Message);
                    continue;
                }

                var rawMsgs = wrapper["messages"] as Newtonsoft.Json.Linq.JArray;

                // Deserialize messages
                receivedMessages = DeserializeMessages(rawMsgs);

                var deserDoneTime = stopwatch.Elapsed.TotalSeconds;

                // Call Unity Update to handle Subs and Pubs in main thread
                stepIndex++;
                stepRequested = true;
                while (stepRequested)
                {
                    // Wait for the step to be processed by Update func calls
                }
                var updateDoneTime = stopwatch.Elapsed.TotalSeconds;


                // Add StepFinishedMessage to envelopes
                Publish("/sim_control/step_finished",
                    new StepFinishedMessage
                    {
                        success = true
                    });
                    /*envelopesToPublish.Add(new MessageEnvelope(
                        topic: "/sim_control/step_finished",
                        type: "StepFinishedMessage",
                        data: new StepFinishedMessage
                        {
                            success = true
                        }
                    ));*/

                // Serialize, send and clear envelopes
                SendAndClearEnvelopes();

                var sendingDoneTime = stopwatch.Elapsed.TotalSeconds;


                var mainloopend = stopwatch.Elapsed.TotalSeconds;
                if (timingVerbose)
                {
                    Debug.Log($"[Timing] Total main loop: {mainloopend:F4} seconds");
                    Debug.Log($"[Timing] Reading time: {readingDoneTime:F4} seconds");
                    Debug.Log($"[Timing] Deserialization time: {(deserDoneTime - readingDoneTime):F4} seconds");
                    Debug.Log($"[Timing] Update processing time: {(updateDoneTime - deserDoneTime):F4} seconds");
                    Debug.Log($"[Timing] Sending time: {(sendingDoneTime - updateDoneTime):F4} seconds");
                }
            }
        }
        /*catch (Exception e)
        {
            Debug.LogWarning("Client connection error: " + e.Message);
        }
        finally*/
        {
            client.Close();
            Debug.Log("Client disconnected");
        }
    }


    static int ResolveListenPort()
    {
        // Allow overriding port via CLI (`-port 9001`) or env var (RATSIM_UNITY_PORT),
        // so multiple Unity instances can coexist on the same host. CLI wins.
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-port" && int.TryParse(args[i + 1], out int p)) return p;
        }
        var envPort = Environment.GetEnvironmentVariable("RATSIM_UNITY_PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int ep)) return ep;
        return 9000;
    }

    void MainLoop()
    {
        int port = ResolveListenPort();
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Debug.Log($"TCP server started on port {port}");

        while (true)
        {
            Debug.Log("Waiting for client...");
            TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true;
            Debug.Log("Client connected");

            Thread clientThread = new Thread(() => HandleClient(client));
            clientThread.IsBackground = true;
            clientThread.Start();
        }
    }
    
    void OnApplicationQuit()
    {
        listener?.Stop();
        serverThread?.Abort();
    }

}
