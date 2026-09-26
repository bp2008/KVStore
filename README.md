# KVStore

A light-weight key-value-store server written in C# and supporting anonymous user access and web-based configuration.

## Purpose

This key-value store server was designed to provide simple applications with a way to store and retrieve temporarly data in the cloud.  The specific goal I had in mind was to facilitate export and import features: One device uploads data to the key-value store and displays a QR code containing key information.  Another device scans the QR code and retrieves the data from the key-value store.  No additional setup or configuration is required of the user; they just need both devices to have internet access and the ability to scan a QR code or input a simple key string.

Specific design decisions were made to facilitate this purpose and reduce the risk of abuse:
* Clients cannot choose what key to use.  The server generates a random reasonably-high-entropy key for each new value that is stored.
* Clients are encouraged to encrypt data before sending it to the server.
* Objects uploaded by clients are automatically deleted after an admin-configurable amount of time (minutes or hours).
* No `GET /{key}` retrieval. **All KV operations are POST.** This prevents the service from being used as a link-shareable file host.
* No `Content-Type` awareness for stored values.  Values are treated as opaque byte arrays.
* No logging of client IP addresses or other identifying information.