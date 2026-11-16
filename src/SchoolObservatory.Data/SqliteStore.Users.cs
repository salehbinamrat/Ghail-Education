using System.Globalization;
using Microsoft.Data.Sqlite;
using SchoolObservatory.Core;

namespace SchoolObservatory.Data;

public sealed partial class SqliteStore
{
    /// <summary>Idempotently upgrades existing single-role accounts without deleting data or passwords.</summary>
    private static void MigrateUserPermissions(SqliteConnection con)
    {
        static bool HasColumn(SqliteConnection connection, string column)
        {
            using var command = Cmd(connection, null, "SELECT COUNT(*) FROM pragma_table_info('users') WHERE name=$name", ("$name", column));
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        var needsMask = !HasColumn(con, "permission_mask");
        var needsAuthVersion = !HasColumn(con, "auth_version");
        var needsOwner = !HasColumn(con, "is_owner");
        if (needsMask)
        {
            using var alter = Cmd(con, null,
                "ALTER TABLE users ADD COLUMN permission_mask INTEGER NOT NULL DEFAULT 0 CHECK(permission_mask >= 0)");
            alter.ExecuteNonQuery();

            // Map old roles to exactly the permissions they had before this migration.
            using var migrate = Cmd(con, null, """
                UPDATE users
                SET permission_mask=CASE role
                    WHEN 'admin' THEN $admin
                    WHEN 'statistician' THEN $statistician
                    WHEN 'reviewer' THEN $reviewer
                    WHEN 'principal' THEN $principal
                    WHEN 'entry' THEN $entry
                    WHEN 'viewer' THEN $viewer
                    ELSE 0 END
                """,
                ("$admin", (long)(PermissionCatalog.All & ~PermissionFlags.ApproveSchool)),
                ("$statistician", (long)PermissionCatalog.DefaultForRole(Roles.Statistician)),
                ("$reviewer", (long)PermissionCatalog.DefaultForRole(Roles.Reviewer)),
                ("$principal", (long)PermissionCatalog.DefaultForRole(Roles.Principal)),
                ("$entry", (long)PermissionCatalog.DefaultForRole(Roles.Entry)),
                ("$viewer", (long)PermissionCatalog.DefaultForRole(Roles.Viewer)));
            migrate.ExecuteNonQuery();
        }
        if (needsOwner)
        {
            using var alter = Cmd(con, null,
                "ALTER TABLE users ADD COLUMN is_owner INTEGER NOT NULL DEFAULT 0 CHECK(is_owner IN (0,1))");
            alter.ExecuteNonQuery();
            // The bootstrap event is the authoritative original owner for a migrated database.
            using var migration = Cmd(con, null, """
                UPDATE users SET is_owner=1,permission_mask=$all
                WHERE id=COALESCE(
                    (SELECT a.actor_id FROM audit_events a JOIN users u ON u.id=a.actor_id
                     WHERE a.action='bootstrap' AND u.role='admin' ORDER BY a.created_utc LIMIT 1),
                    (SELECT id FROM users WHERE role='admin' ORDER BY created_utc,id LIMIT 1))
                AND NOT EXISTS(SELECT 1 FROM users WHERE is_owner=1)
                """, ("$all", (long)PermissionCatalog.All));
            migration.ExecuteNonQuery();
        }
        if (needsAuthVersion)
        {
            using var alter = Cmd(con, null,
                "ALTER TABLE users ADD COLUMN auth_version INTEGER NOT NULL DEFAULT 0 CHECK(auth_version >= 0)");
            alter.ExecuteNonQuery();
        }
        using var index = Cmd(con, null, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_users_single_owner
            ON users(is_owner) WHERE is_owner=1;
            """);
        index.ExecuteNonQuery();
    }

    private void RequireFreshActor(UserAccount actor)
    {
        if (!actor.IsActive) throw new UnauthorizedAccessException("الجلسة غير نشطة.");
        using var con = Open();
        using var cmd = Cmd(con, null,
            "SELECT role,school_id,is_active,permission_mask,is_owner,auth_version FROM users WHERE id=$id",
            ("$id", actor.Id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(2) != 1 ||
            Text(reader, 0) != actor.Role ||
            NullableText(reader, 1) != actor.SchoolId ||
            reader.GetInt64(3) != actor.PermissionMask ||
            (reader.GetInt32(4) == 1) != actor.IsOwner ||
            reader.GetInt64(5) != actor.AuthVersion)
            throw new UnauthorizedAccessException("تغيرت صلاحيات الحساب أو حالته. أغلق الجلسة وسجل الدخول مجددا.");
    }

    private static UserAccount ReadAccount(SqliteDataReader r) =>
        new(Text(r, 0), Text(r, 1), Text(r, 2), NullableText(r, 3),
            r.GetInt32(4) == 1, r.GetInt64(5), r.GetInt32(6) == 1, r.GetInt64(7));

    /// <summary>Updates a user without allowing delegated managers to elevate privileges.</summary>
    public void UpdateUserPermissions(UserAccount actor, string targetId, long permissionMask)
    {
        RequireFreshActor(actor);
        if (!PermissionCatalog.IsValid(permissionMask))
            throw new ArgumentException("يجب اختيار صلاحية عرض المدارس مع صلاحيات صالحة.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var read = Cmd(con, tx,
            "SELECT id,username,role,school_id,is_active,permission_mask,is_owner,auth_version FROM users WHERE id=$id",
            ("$id", targetId));
        UserAccount target;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) throw new ArgumentException("المستخدم غير موجود.");
            target = ReadAccount(r);
        }
        if (target.IsOwner)
        {
            if (actor.Id != target.Id) throw new UnauthorizedAccessException("لا يمكنك تعديل صلاحيات المسؤول المؤسس.");
            throw new InvalidOperationException("صلاحيات المسؤول المؤسس محمية ولا يمكن تعديلها من إدارة المستخدمين.");
        }
        if (!AccessPolicy.CanManageAccount(actor, target))
            throw new UnauthorizedAccessException("لا يمكنك تغيير صلاحيات هذا المسؤول.");
        if (!PermissionCatalog.IsRoleAllowed(target.Role, permissionMask))
            throw new ArgumentException("الصلاحيات المختارة لا تتوافق مع نوع الحساب.");
        if (!actor.IsOwner && ((permissionMask & ~actor.PermissionMask) != 0 ||
                               ((PermissionFlags)permissionMask).HasFlag(PermissionFlags.ManageUsers)))
            throw new UnauthorizedAccessException("لا يمكن تفويض صلاحيات أكبر من صلاحياتك.");
        using var update = Cmd(con, tx, "UPDATE users SET permission_mask=$p, auth_version=auth_version+1 WHERE id=$id",
            ("$p", permissionMask), ("$id", targetId));
        update.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "user.permissions", targetId, $"permissions={permissionMask}");
        tx.Commit();
    }

    /// <summary>Deactivates an account without deleting its audit trail or creating an orphaned school.</summary>
    public void SetUserActive(UserAccount actor, string targetId, bool active)
    {
        RequireFreshActor(actor);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var read = Cmd(con, tx,
            "SELECT id,username,role,school_id,is_active,permission_mask,is_owner,auth_version FROM users WHERE id=$id",
            ("$id", targetId));
        UserAccount target;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) throw new ArgumentException("المستخدم غير موجود.");
            target = ReadAccount(r);
        }
        if (target.IsOwner || actor.Id == target.Id)
            throw new InvalidOperationException("لا يمكن تعطيل المسؤول المؤسس أو الحساب المستخدم حاليا.");
        if (!AccessPolicy.CanManageAccount(actor, target))
            throw new UnauthorizedAccessException("لا يمكنك إدارة هذا الحساب.");
        using var update = Cmd(con, tx,
            "UPDATE users SET is_active=$active,failed_attempts=0,lock_until_utc=NULL,auth_version=auth_version+1 WHERE id=$id",
            ("$active", active ? 1 : 0), ("$id", targetId));
        update.ExecuteNonQuery();
        Audit(con, tx, actor.Id, active ? "user.activate" : "user.deactivate", targetId, "");
        tx.Commit();
    }

    public void ResetUserPassword(UserAccount actor, string targetId, string newPassword)
    {
        RequireFreshActor(actor);
        var hashed = PasswordService.Hash(newPassword);
        using var con = Open();
        using var tx = con.BeginTransaction();
        using var read = Cmd(con, tx,
            "SELECT id,username,role,school_id,is_active,permission_mask,is_owner,auth_version FROM users WHERE id=$id",
            ("$id", targetId));
        UserAccount target;
        using (var r = read.ExecuteReader())
        {
            if (!r.Read()) throw new ArgumentException("المستخدم غير موجود.");
            target = ReadAccount(r);
        }
        if (!AccessPolicy.CanManageAccount(actor, target))
            throw new UnauthorizedAccessException("لا يمكنك إعادة تعيين كلمة مرور هذا الحساب.");
        using var update = Cmd(con, tx,
            "UPDATE users SET password_hash=$hash,failed_attempts=0,lock_until_utc=NULL,auth_version=auth_version+1 WHERE id=$id",
            ("$hash", hashed), ("$id", targetId));
        update.ExecuteNonQuery();
        Audit(con, tx, actor.Id, "user.password_reset", targetId, "Password reset; no secret logged");
        tx.Commit();
    }
}
