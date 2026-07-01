const { spawn } = require("child_process");

const url = process.argv[2] || "http://localhost:5096";

const commands = {
    win32: ["cmd", ["/c", "start", "", url]],
    darwin: ["open", [url]],
    linux: ["xdg-open", [url]],
};

const [command, args] = commands[process.platform] ?? commands.linux;

spawn(command, args, { detached: true, stdio: "ignore" }).unref();
