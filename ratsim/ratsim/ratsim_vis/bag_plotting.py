from roslike_unity_connector.bag import MessageBag
from roslike_unity_connector.message_definitions import PoseMessage
import matplotlib.pyplot as plt
from nav.utils import *
import numpy as np

import sys
import time
import os

def plot_trajs():
    # TODO - rewrite to have 1st argument be the target pose2D topic

    print("Num filenames" + str(len(sys.argv) - 1))
    filenames = sys.argv[1:]
    pose_topic = "/rat1_pose"

    if not filenames:
        print("Usage: python script.py file1.bag [file2.bag ...]")
        return

    plt.figure()

    for filename in filenames:
        bag = MessageBag(filename)
        print(f"{filename}: num steps = {len(bag.steps)}")

        x, z = [], []

        for step in bag.steps:
            if pose_topic not in step:
                continue
            pose_msg = step[pose_topic][0]
            z.append(pose_msg.x)
            x.append(-pose_msg.y)  # Invert left for Unity-style x

        label = os.path.basename(filename)
        plt.plot(x, z, label=label)

    plt.xlabel("x (unity)")
    plt.ylabel("z (unity)")
    plt.gca().set_aspect("equal")
    plt.legend()
    plt.title("Trajectories")
    plt.show()

def plot_traj():
    save_filename = sys.argv[1]
    
    bag = MessageBag(save_filename)
    print("num steps: " + str(len(bag.steps)))

    z = []
    x = []
    pose_topic = "/rat1_pose"
    pose_topic2 = "/rat1_pose_noised"

    noised_z = []
    noised_x = []

    dif = 0 

    for step in bag.steps:
        if pose_topic in step.keys():
            pose_msg = step[pose_topic][0]
            z.append(pose_msg.x)
            x.append(-pose_msg.y)

        if pose_topic2 in step.keys():
            pose_msg2 = step[pose_topic2][0]
            noised_z.append(pose_msg2.x)
            noised_x.append(-pose_msg2.y)

        if pose_topic in step.keys() and pose_topic2 in step.keys():
            pose_msg = step[pose_topic][0]
            pose_msg2 = step[pose_topic2][0]
            dist = np.linalg.norm(np.array([pose_msg.x, -pose_msg.y]) - np.array([pose_msg2.x, -pose_msg2.y]))
            dif += dist

    print("Average difference between poses: " + str(dif / len(bag.steps)))

    # plt.plot(x, z)
    # plt.xlabel("x (unity)")
    # plt.ylabel("z (unity)")

    plt.plot(x, z, label="Pose")
    plt.plot(noised_x, noised_z, label="Noised Pose", linestyle='--')
    plt.legend()


    plt.gca().set_aspect("equal")
    plt.show()

def plot_data_cont():
    save_filename = sys.argv[1]
    skip_factor = int(sys.argv[2]) if len(sys.argv) >= 3 else 1
    margin = int(sys.argv[3]) if len(sys.argv) >= 4 else 200
    bag = MessageBag(save_filename)
    lidar_size = int(6 * margin / 100)
    lidar_size = max(lidar_size, 1)  # Ensure minimum size for visibility
    
    print("num steps:", len(bag.steps))
    
    z = []
    x = []
    pose_topic = "/rat1_pose"
    lidar_topic = "/lidar2d"
    visual_tracker_topic = "/visual_point_track_pcl"
    
    plt.ion()  # Turn on interactive mode
    fig, ax = plt.subplots()
    line, = ax.plot([], [], '-')  # Blue circles with lines
    lidar_scatter = ax.scatter([], [], c='r', s=3, label="Lidar")  # Lidar points
    ax.set_xlabel("x (unity)")
    ax.set_ylabel("z (unity)")
    ax.set_aspect("equal")
    ax.legend()
    
    for i, step in enumerate(bag.steps):
        if not i % skip_factor == 0:
            continue
        if pose_topic not in step:
            continue
        pose_msg = step[pose_topic][0]
        z.append(pose_msg.x)
        x.append(-pose_msg.y)
    
        # Draw lidar msg
        rgb_colors = "r"
        if lidar_topic in step and len(step[lidar_topic]) > 0:
            print("lidar msg!")
            lidar_msg = step[lidar_topic][0]
            pcl = lidar2d_to_pointcloud(lidar_msg)
            pcl_world = transform_pointcloud2d(pcl, pose_msg)
            colors = lidar_msg.descriptors #is list of lists with 3 elements each
            rgb_colors = np.array(colors).reshape(-1, 3)
            valid_mask = getLidarValidMask(np.array(lidar_msg.ranges), lidar_msg.maxRange)
            rgb_colors = rgb_colors[valid_mask]
            print(colors)

            # pcl_world = pcl

            # Plot transformed lidar points
            if pcl_world.size > 0:
                print("aa msg!")
                # Split into x and z (remember: z is forward)
                lidar_x = -pcl_world[:, 1]
                lidar_z = pcl_world[:, 0]

                lidar_scatter.remove()
                lidar_scatter.set_offsets(np.c_[lidar_x, lidar_z])
                print(lidar_x.shape)
                print(lidar_z.shape)
                print(rgb_colors.shape)
                lidar_scatter = ax.scatter(lidar_x, lidar_z, c=rgb_colors, s=lidar_size, label="Lidar")

        elif visual_tracker_topic in step:
            print("visual msg!")
            visual_msg = step[visual_tracker_topic][0]
            pcl, descriptors = visual_tracker_msg_to_pointcloud3d(visual_msg)
            pcl_world = transform_pointcloud2d(pcl[:, :2], pose_msg)
            rgb_colors = descriptors

            # Plot transformed lidar points
            if pcl_world.size > 0:
                print("aa msg!")
                lidar_x = -pcl_world[:, 1]
                lidar_z = pcl_world[:, 0]
                # lidar_x = pcl_world[:, 0]
                # lidar_z = pcl_world[:, 1]

                lidar_scatter.remove()
                lidar_scatter.set_offsets(np.c_[lidar_x, lidar_z])
                # print(lidar_x.shape)
                # print(lidar_z.shape)
                # print(rgb_colors.shape)
                lidar_scatter = ax.scatter(lidar_x, lidar_z, c=rgb_colors, s=lidar_size, label="Lidar")

        # Update plot data
        line.set_xdata(x)
        line.set_ydata(z)
    
        # ax.relim()
        # ax.autoscale_view()
        
        # ax.set_xlim(-30, 30)  # x-axis range
        # ax.set_ylim(-30, 30)  # z-axis range

        # Combine trajectory and lidar points
        # all_x = np.concatenate((x, lidar_x)) if 'lidar_x' in locals() else np.array(x)
        # all_z = np.concatenate((z, lidar_z)) if 'lidar_z' in locals() else np.array(z)
        all_x = np.array(x)
        all_z = np.array(z)
        
        # Compute bounds with margin
        x_min, x_max = np.min(all_x) - margin, np.max(all_x) + margin
        z_min, z_max = np.min(all_z) - margin, np.max(all_z) + margin
        
        # Set axis limits
        ax.set_xlim(x_min, x_max)
        ax.set_ylim(z_min, z_max)
    
        plt.draw()
        plt.pause(0.0001)  # Allow GUI events to be processed
    
        # Print the current step data
        print(f"Step {i}: x = {x[-1]:.3f}, z = {z[-1]:.3f}")
    
        # time.sleep(0.001)  # Sleep for 100 ms between steps

# Keep plot open after loop finishes
plt.ioff()
plt.show()

