#!/usr/bin/env python3
"""Simple GTK4/libadwaita front-end for the official proton-drive CLI.

Unofficial; not affiliated with Proton AG.
Run:  python3 app.py
Needs: python3-gi, gir1.2-gtk-4.0, gir1.2-adw-1  (Debian/Ubuntu names)
"""
import posixpath
import sys
import threading

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk  # noqa: E402

from protondrive import ROOT, DriveCLI, DriveError, looks_like_auth_error  # noqa: E402


def human(n):
    if n is None:
        return ""
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024


class Window(Adw.ApplicationWindow):
    def __init__(self, app, cli):
        super().__init__(application=app, title="Proton Drive", default_width=760, default_height=560)
        self.cli = cli
        self.cwd = ROOT
        self.history = []

        self.toasts = Adw.ToastOverlay()
        view = Adw.ToolbarView()
        header = Adw.HeaderBar()

        self.back_btn = Gtk.Button(icon_name="go-previous-symbolic", tooltip_text="Back")
        self.back_btn.connect("clicked", lambda *_: self.go_back())
        self.up_btn = Gtk.Button(icon_name="go-up-symbolic", tooltip_text="Parent folder")
        self.up_btn.connect("clicked", lambda *_: self.go_up())
        header.pack_start(self.back_btn)
        header.pack_start(self.up_btn)

        refresh = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="Refresh")
        refresh.connect("clicked", lambda *_: self.refresh())
        upload = Gtk.Button(icon_name="document-send-symbolic", tooltip_text="Upload files here")
        upload.connect("clicked", self.on_upload_clicked)
        header.pack_end(refresh)
        header.pack_end(upload)

        self.path_label = Gtk.Label(xalign=0, margin_start=12, margin_top=6, margin_bottom=6)
        self.path_label.add_css_class("dim-label")
        self.spinner = Gtk.Spinner(margin_end=12)

        self.listbox = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        self.listbox.add_css_class("boxed-list")
        self.listbox.set_margin_start(12)
        self.listbox.set_margin_end(12)
        self.listbox.set_margin_bottom(12)
        self.listbox.connect("row-activated", self.on_row_activated)
        scroller = Gtk.ScrolledWindow(vexpand=True)
        scroller.set_child(self.listbox)

        bar = Gtk.Box()
        bar.append(self.path_label)
        self.path_label.set_hexpand(True)
        bar.append(self.spinner)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        box.append(bar)
        box.append(scroller)

        view.add_top_bar(header)
        view.set_content(box)
        self.toasts.set_child(view)
        self.set_content(self.toasts)

        # Drag & drop files from the file manager to upload into the current folder
        drop = Gtk.DropTarget.new(Gdk.FileList, Gdk.DragAction.COPY)
        drop.connect("drop", self.on_drop)
        self.listbox.add_controller(drop)

        self.refresh()

    # --- helpers -----------------------------------------------------
    def toast(self, msg):
        self.toasts.add_toast(Adw.Toast(title=msg, timeout=4))

    def run_bg(self, work, done):
        """Run `work()` in a thread, then `done(result, error)` on the UI thread."""
        self.spinner.start()

        def target():
            try:
                res, err = work(), None
            except DriveError as e:
                res, err = None, str(e)
            GLib.idle_add(finish, res, err)

        def finish(res, err):
            self.spinner.stop()
            done(res, err)
            return False

        threading.Thread(target=target, daemon=True).start()

    # --- navigation ----------------------------------------------------
    def navigate(self, path, push=True):
        if push and path != self.cwd:
            self.history.append(self.cwd)
        self.cwd = path
        self.refresh()

    def go_back(self):
        if self.history:
            self.navigate(self.history.pop(), push=False)

    def go_up(self):
        if self.cwd != ROOT:
            self.navigate(posixpath.dirname(self.cwd) or ROOT)

    def refresh(self):
        self.path_label.set_text(self.cwd)
        self.back_btn.set_sensitive(bool(self.history))
        self.up_btn.set_sensitive(self.cwd != ROOT)
        cwd = self.cwd
        self.run_bg(lambda: self.cli.list(cwd), lambda r, e: self.show(r, e, cwd))

    def show(self, entries, err, cwd):
        if cwd != self.cwd:
            return  # stale result
        while (row := self.listbox.get_first_child()) is not None:
            self.listbox.remove(row)
        if err:
            if looks_like_auth_error(err):
                self.add_login_row()
            else:
                self.toast(err.splitlines()[0][:200])
            return
        if not entries:
            self.listbox.append(Adw.ActionRow(title="This folder is empty"))
        for e in entries:
            row = Adw.ActionRow(title=GLib.markup_escape_text(e.name), subtitle=human(e.size))
            row.add_prefix(Gtk.Image.new_from_icon_name("folder-symbolic" if e.is_dir else "text-x-generic-symbolic"))
            row.entry = e
            row.set_activatable(e.is_dir)
            dl = Gtk.Button(icon_name="document-save-symbolic", valign=Gtk.Align.CENTER, tooltip_text="Download")
            dl.add_css_class("flat")
            dl.connect("clicked", self.on_download_clicked, e)
            row.add_suffix(dl)
            self.listbox.append(row)

    def add_login_row(self):
        row = Adw.ActionRow(title="Not signed in", subtitle="Opens your browser to sign in to Proton")
        btn = Gtk.Button(label="Sign in", valign=Gtk.Align.CENTER)
        btn.add_css_class("suggested-action")
        btn.connect("clicked", self.on_login)
        row.add_suffix(btn)
        self.listbox.append(row)

    def on_row_activated(self, _lb, row):
        e = getattr(row, "entry", None)
        if e and e.is_dir:
            self.navigate(e.path)

    # --- actions -------------------------------------------------------
    def on_login(self, *_):
        self.toast("Complete sign-in in your browser…")
        self.run_bg(self.cli.login, lambda r, e: (self.toast(e.splitlines()[0]) if e else self.refresh()))

    def on_upload_clicked(self, *_):
        dialog = Gtk.FileDialog(title="Upload to " + self.cwd)

        def chosen(d, res):
            try:
                files = d.open_multiple_finish(res)
            except GLib.Error:
                return
            self.upload([f.get_path() for f in files])

        dialog.open_multiple(self, None, chosen)

    def on_drop(self, _t, value, _x, _y):
        self.upload([f.get_path() for f in value.get_files() if f.get_path()])
        return True

    def upload(self, paths):
        if not paths:
            return
        dest = self.cwd
        self.toast(f"Uploading {len(paths)} item(s)…")
        self.run_bg(lambda: self.cli.upload(paths, dest),
                    lambda r, e: (self.toast("Upload failed: " + e.splitlines()[0]) if e
                                  else (self.toast("Upload finished"), self.refresh())))

    def on_download_clicked(self, _btn, entry):
        dialog = Gtk.FileDialog(title=f"Download “{entry.name}” to…")

        def chosen(d, res):
            try:
                folder = d.select_folder_finish(res).get_path()
            except GLib.Error:
                return
            self.toast(f"Downloading {entry.name}…")
            self.run_bg(lambda: self.cli.download(entry.path, folder, entry.size),
                        lambda r, e: self.toast("Download failed: " + e.splitlines()[0] if e
                                                else f"Saved to {folder}"))

        dialog.select_folder(self, None, chosen)


class App(Adw.Application):
    def __init__(self):
        super().__init__(application_id="dev.example.ProtonDriveGui", flags=Gio.ApplicationFlags.FLAGS_NONE)

    def do_activate(self):
        try:
            cli = DriveCLI()
        except DriveError as e:
            dlg = Adw.AlertDialog(heading="proton-drive CLI not found", body=str(e))
            dlg.add_response("ok", "OK")
            win = Adw.ApplicationWindow(application=self)
            win.present()
            dlg.choose(win, None, lambda *_: self.quit())
            return
        Window(self, cli).present()


if __name__ == "__main__":
    sys.exit(App().run(sys.argv))
