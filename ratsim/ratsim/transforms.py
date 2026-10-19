import math


def quat_from_yaw(yaw_rad):
    """Convert yaw (radians, CCW around z-up) to quaternion (qx, qy, qz, qw)."""
    qx = 0.0
    qy = 0.0
    qz = math.sin(yaw_rad / 2.0)
    qw = math.cos(yaw_rad / 2.0)
    return qx, qy, qz, qw


def yaw_from_quat(qx, qy, qz, qw):
    """Extract yaw (radians, in [-pi, pi]) from quaternion."""
    return math.atan2(2.0 * qw * qz, 1.0 - 2.0 * qz * qz)


def quat_to_rpy(qx, qy, qz, qw):
    """Convert quaternion to (roll, pitch, yaw) in radians."""
    # Roll (x-axis rotation)
    sinr_cosp = 2.0 * (qw * qx + qy * qz)
    cosr_cosp = 1.0 - 2.0 * (qx * qx + qy * qy)
    roll = math.atan2(sinr_cosp, cosr_cosp)

    # Pitch (y-axis rotation)
    sinp = 2.0 * (qw * qy - qz * qx)
    if abs(sinp) >= 1.0:
        pitch = math.copysign(math.pi / 2.0, sinp)
    else:
        pitch = math.asin(sinp)

    # Yaw (z-axis rotation)
    siny_cosp = 2.0 * (qw * qz + qx * qy)
    cosy_cosp = 1.0 - 2.0 * (qy * qy + qz * qz)
    yaw = math.atan2(siny_cosp, cosy_cosp)

    return roll, pitch, yaw


def rpy_to_quat(roll, pitch, yaw):
    """Convert (roll, pitch, yaw) in radians to quaternion (qx, qy, qz, qw)."""
    cr = math.cos(roll / 2.0)
    sr = math.sin(roll / 2.0)
    cp = math.cos(pitch / 2.0)
    sp = math.sin(pitch / 2.0)
    cy = math.cos(yaw / 2.0)
    sy = math.sin(yaw / 2.0)

    qx = sr * cp * cy - cr * sp * sy
    qy = cr * sp * cy + sr * cp * sy
    qz = cr * cp * sy - sr * sp * cy
    qw = cr * cp * cy + sr * sp * sy

    return qx, qy, qz, qw
