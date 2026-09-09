package android.os;
import java.io.File;
public class Environment {
    public static final String DIRECTORY_DOWNLOADS = "Download";
    public static final String DIRECTORY_MOVIES = "Movies";
    public static final String DIRECTORY_MUSIC = "Music";
    public static final String DIRECTORY_PICTURES = "Pictures";
    public static File getExternalStorageDirectory() { return new File("."); }
    public static File getExternalStoragePublicDirectory(String type) { return new File("."); }
    public static boolean isExternalStorageEmulated() { return true; }
    public static String getExternalStorageState() { return "mounted"; }
    public static boolean isExternalStorageLegacy() { return true; }
}
