window.setVideoVolume = (id, volume) => {
    const video = document.getElementById(id);
    if (video) {
        video.volume = volume;
    }
};