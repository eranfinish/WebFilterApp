package com.urlfilter.models;

import java.util.Date;

public class DeviceUser {
    public int id;
    public String username;
    public String displayName;
    public UserType userType = UserType.ADULT;
    public Integer age;
    public Date profileCreatedDate = new Date();
    public boolean isActive = true;
    public Integer parentUserId;
    public String avatarPath;
    public Date lastLogin;
    public Integer createdBy;

    public DeviceUser() {}

    public DeviceUser(String username, String displayName, UserType userType) {
        this.username = username;
        this.displayName = displayName;
        this.userType = userType;
    }

    public void setUserType(UserType t) { this.userType = t; }
    public UserType getUserType() { return userType; }
    public void setUsername(String u) { this.username = u; }
    public String getUsername() { return username; }
    public void setDisplayName(String d) { this.displayName = d; }
    public String getDisplayName() { return displayName; }
    public int getId() { return id; }
    public void setId(int id) { this.id = id; }
}
