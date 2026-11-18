package app.deskpanel

import android.Manifest
import android.content.ContentProvider
import android.content.ContentValues
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.os.Binder
import android.os.Bundle

/**
 * Receives the device key from the PC agent over USB:
 *   adb shell content call --uri content://app.deskpanel.enrol --method enrol --arg <deviceId> --extra key:s:<key>
 *   adb shell content call --uri content://app.deskpanel.enrol --method status
 * call() is not covered by the manifest's android:permission, so every call is checked here: it must come from the
 * adb shell (uid 2000), which also holds DUMP. No other app can plant a key or learn which device id this phone has.
 */
class EnrolProvider : ContentProvider() {

    override fun onCreate() = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        if (!fromAdbShell()) throw SecurityException("enrolment is only accepted from adb")
        val vault = KeyVault(context!!)
        return when (method) {
            "status" -> Bundle().apply {
                val e = vault.load()
                putBoolean("enrolled", e != null)
                if (e != null) putString("id", e.deviceId)
            }
            "enrol" -> {
                val key = extras?.getString("key")
                val ok = Proof.isDeviceId(arg) && Proof.isKey(key)
                if (ok) vault.store(arg!!, key!!)
                Bundle().apply { putBoolean("ok", ok) }
            }
            else -> null
        }
    }

    private fun fromAdbShell(): Boolean =
        Binder.getCallingUid() == SHELL_UID &&
            context!!.checkCallingPermission(Manifest.permission.DUMP) == PackageManager.PERMISSION_GRANTED

    // No table access of any kind.
    override fun query(uri: Uri, p: Array<out String>?, s: String?, a: Array<out String>?, o: String?): Cursor? = null
    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, s: String?, a: Array<out String>?) = 0
    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<out String>?) = 0

    private companion object {
        const val SHELL_UID = 2000 // android.os.Process.SHELL_UID
    }
}
