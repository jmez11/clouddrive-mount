---
layout: page
title: Google Drive
---

CloudDrive Mount mounts your Google Drive as a readable and writable drive letter. It uses **your own OAuth client** — there is no shared OceanSideVR Google project, so your tokens are yours alone.

## Set up your own OAuth client

1. Go to [Google Cloud Console](https://console.cloud.google.com/) and create a project (or pick an existing one).
2. Enable the **Google Drive API** for the project.
3. Configure the **OAuth consent screen** (External user type is fine for personal use).
4. Create an **OAuth client ID** of application type **Desktop app**.
5. Download the `client_secret_*.json` file for that client.

Then, in CloudDrive Mount, add a Google Drive drive:

- Click **Import** and choose the `client_secret_*.json` file, or paste the **Client ID** and **Client Secret** into their boxes.
- Pick a scope: **My Drive**, **Shared with me**, or a single **Folder** (paste the folder ID from the folder's Drive URL).
- Click **Sign in with Google...**.

## The unverified-app warning

When you sign in, Google shows an **"unverified app" warning**. That's expected — this app isn't verified by Google. Click **Advanced**, then **Go to** (your app name) to continue.

## How sign-in works

Sign-in uses OAuth 2.0 with PKCE and a loopback redirect on your own computer — the browser talks back to the app on `127.0.0.1`, and no credentials pass through any third party. The app requests the `https://www.googleapis.com/auth/drive` scope, which allows it to see and manage your Drive files, because it mounts your Drive as a readable and writable drive.

Your access and refresh tokens are stored encrypted on your device (Windows DPAPI) and sent only to Google. Expired tokens are refreshed automatically; if a refresh fails, the app recovers by signing in again on the next attempt.

## Google Docs, Sheets, and Slides

Native Google files can't be downloaded as regular files, so they appear on the mounted drive as `.url` Internet shortcuts. Opening one opens the document in your browser.

## Revoking access

To disconnect the app from your Google account, remove its access in your Google account's [third-party access settings](https://myaccount.google.com/permissions), then delete the drive in CloudDrive Mount.
