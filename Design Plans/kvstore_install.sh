#!/bin/bash

AppName="kvstore"
GithubRepo="bp2008/KVStore"
ExeName="KVStoreLinux.dll"
AssemblyName="KVStoreLinux"

################################################################
# Function: Install the .NET 10.0 runtime if not already present.
################################################################
InstallDotnetIfNotAlready () {
	# Check if .NET 10.0 is already installed
	if ! dotnet --list-runtimes 2> /dev/null | grep -q 'Microsoft.NETCore.App 10.0'; then
	    # Install .NET 10.0 from OS-provided packages
	    if [ -f /etc/os-release ]; then
	        # shellcheck disable=SC1091
	        . /etc/os-release
	        if [ "$ID" == "ubuntu" ] && [ "${VERSION_ID%.*}" -ge "22" ]; then
	            sudo apt-get update
	            # Ubuntu 24.04+ ships dotnet-runtime-10.0 directly in its own archive.
	            # Ubuntu 22.04's archive does not have it yet, so fall back to Canonical's
	            # .NET backports PPA, but only if the package isn't already available.
	            if ! apt-cache show dotnet-runtime-10.0 &> /dev/null; then
	                if [ "${VERSION_ID%.*}" -eq "22" ]; then
	                    echo "dotnet-runtime-10.0 not found in the default repositories; adding ppa:dotnet/backports"
	                    if ! command -v add-apt-repository &> /dev/null; then
	                        sudo apt-get install -y software-properties-common
	                    fi
	                    sudo add-apt-repository -y ppa:dotnet/backports
	                    sudo apt-get update
	                else
	                    echo "dotnet-runtime-10.0 was not found in the default repositories for this Ubuntu release"
	                    exit 1
	                fi
	            fi
	            sudo apt-get install -y dotnet-runtime-10.0
	        elif [ "$ID" == "rhel" ] && [ "${VERSION_ID%.*}" -ge "8" ]; then
	            # .NET 10.0 is included in the RHEL AppStream repositories; no extra repo needed.
	            sudo yum install -y dotnet-sdk-10.0
	        elif [ "$ID" == "amzn" ] && [ "$VERSION_ID" != "2" ]; then
	            # Amazon Linux 2023+ ships dotnet-sdk-10.0 directly; no Microsoft repo needed.
	            sudo yum install -y dotnet-sdk-10.0
	        else
	            echo "Unsupported Linux distribution for installing .NET 10.0 from OS-provided packages"
	            exit 1
	        fi
	    else
	        echo "Unsupported Linux distribution for installing .NET 10.0 from OS-provided packages"
	        exit 1
	    fi
	fi
}

##################################################################
# Function: Install a package if the given command is missing.
# Argument 1: command name to test for
# Argument 2: package name to install
##################################################################
InstallPackageIfNotAlready () {
    CommandName="$1";
    PackageName="$2";
    if ! command -v "$CommandName" &> /dev/null; then
        if [ -f /etc/os-release ]; then
            # shellcheck disable=SC1091
            . /etc/os-release;
            if [ "$ID" == "ubuntu" ] || [ "$ID" == "debian" ]; then
                sudo apt-get update;
                sudo apt-get install -y "$PackageName";
            elif [ "$ID" == "rhel" ] || [ "$ID" == "centos" ] || [ "$ID" == "fedora" ] || { [ "$ID" == "amzn" ] && [ "${VERSION_ID%.*}" -ge "2" ]; }; then
                sudo yum install -y "$PackageName";
            elif [ "$ID" == "arch" ]; then
                sudo pacman -Sy "$PackageName";
            else
                echo "Unsupported Linux distribution for installing $PackageName";
                exit 1;
            fi;
        else
            echo "Unsupported Linux distribution for installing $PackageName";
            exit 1;
        fi;
    fi;
}

##################################################################
# Function: Offer to install cloudflared and note next steps.
##################################################################
OfferCloudflaredInstall () {
    if command -v cloudflared &> /dev/null; then
        echo "cloudflared is already installed.";
        return;
    fi

    echo;
    echo "$AppName is designed to be exposed to the internet through a Cloudflare Tunnel,";
    echo "which requires no inbound firewall ports and never reveals this server's IP address.";
    read -r -p "Install cloudflared now? [y/N]: " CfChoice;
    if [[ ! "$CfChoice" =~ ^[Yy]$ ]]; then
        echo "Skipping cloudflared. $AppName will listen on 127.0.0.1 only until a tunnel or";
        echo "reverse proxy is configured.";
        return;
    fi

    if [ -f /etc/os-release ]; then
        # shellcheck disable=SC1091
        . /etc/os-release;
        if [ "$ID" == "ubuntu" ] || [ "$ID" == "debian" ]; then
            Arch=$(dpkg --print-architecture);
            wget -q -O /tmp/cloudflared.deb "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${Arch}.deb" \
                && sudo dpkg -i /tmp/cloudflared.deb \
                && rm -f /tmp/cloudflared.deb;
        else
            echo "Automatic cloudflared install is only scripted for Debian/Ubuntu.";
            echo "See https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/";
            return;
        fi;
    fi

    echo;
    echo "cloudflared installed. To finish setting up the tunnel, run:";
    echo "    cloudflared tunnel login";
    echo "    cloudflared tunnel create $AppName";
    echo "    cloudflared tunnel route dns $AppName kv.example.com";
    echo "  then point the tunnel ingress at http://127.0.0.1:8080 and run:";
    echo "    sudo cloudflared service install";
    echo;
    echo "IMPORTANT: Do NOT route the admin port through the tunnel.";
}

#########################################
# Uninstallation.
#########################################

uninstallApp () {
	echo Uninstalling $AppName.
	echo "The .NET 10.0 runtime and other dependencies will remain installed."
	echo "The application's settings, database, and stored blobs remain in \"/usr/share/$AssemblyName\" and may be deleted manually if you wish."

	cd ~ || exit 1
	sudo /usr/bin/dotnet "$(pwd)/$AppName/$ExeName" uninstall
	sudo rm -r -f "$AppName"
}

#########################################
# Installation.
#########################################

installAndRun () {

echo Beginning installation of $AppName.
echo To uninstall, run this script with the argument "-u"

#########################################
echo Step 1/5: Install .NET 10.0 runtime.
#########################################

InstallDotnetIfNotAlready

#########################################
echo Step 2/5: Install jq and unzip if necessary.
#########################################

InstallPackageIfNotAlready jq jq
InstallPackageIfNotAlready unzip unzip
InstallPackageIfNotAlready wget wget

##########################################################
echo Step 3/5: Download and extract the latest release.
##########################################################

# Navigate to the home directory.
cd ~ || exit 1;

# Get the release information from the GitHub API and extract the tag names using jq.
Releases=$(curl -s https://api.github.com/repos/"$GithubRepo"/releases | jq -r '.[] | .tag_name' | head -n 20);

if [ -z "$Releases" ]; then
    echo "Could not retrieve the release list from GitHub. Check network access and rate limits.";
    exit 1;
fi

# Display the list of releases to the user.
echo "Available releases:";
counter=1;
while read -r line; do
    echo "$counter) $line";
    counter=$((counter+1));
done <<< "$Releases"

# Prompt the user to choose a release.
read -r -p "Enter the number of the release you want to install (default is 1): " ReleaseChoice;

# Set default choice to 1 if no input is given.
if [ -z "$ReleaseChoice" ]; then
    ReleaseChoice=1;
fi

# Get the tag name of the chosen release.
ReleaseTag=$(echo "$Releases" | sed "${ReleaseChoice}q;d");

if [ -z "$ReleaseTag" ]; then
    echo "Invalid selection.";
    exit 1;
fi

echo "Installing Release $ReleaseTag"

# Get the download URL for the chosen release.
ReleaseUrl=$(curl -s https://api.github.com/repos/"$GithubRepo"/releases/tags/"$ReleaseTag" | jq -r '.assets[] | select(.name | contains("Linux")) | .browser_download_url');

if [ -z "$ReleaseUrl" ]; then
    echo "Release $ReleaseTag has no asset with \"Linux\" in the name.";
    exit 1;
fi

# Set the release file name to the variable "ReleaseFile".
ReleaseFile=${ReleaseUrl##*/};

# Download the chosen release using wget.
wget -q -O "$ReleaseFile" "$ReleaseUrl";

# Ensure that the application directory exists.
mkdir -p "$AppName";

# Unzip the release using unzip.
unzip -q -o "$ReleaseFile" -d "$AppName";

###########################################################
echo Step 4/5: Configure program to start automatically.
###########################################################

echo Creating $AppName service: sudo /usr/bin/dotnet \""$(pwd)/$AppName/$ExeName"\" install
sudo /usr/bin/dotnet "$(pwd)/$AppName/$ExeName" install

echo Starting $AppName service: sudo /usr/bin/dotnet \""$(pwd)/$AppName/$ExeName"\" restart
sudo /usr/bin/dotnet "$(pwd)/$AppName/$ExeName" restart

###########################################################
echo Step 5/5: Optional Cloudflare Tunnel setup.
###########################################################

OfferCloudflaredInstall

echo;
echo "$AppName installation complete.";
echo "Public API listens on 127.0.0.1:8080 by default.";
echo "Admin interface listens on port 8081 by default and should be reached over VPN or an SSH tunnel only.";
echo "Run 'sudo /usr/bin/dotnet \"$(pwd)/$AppName/$ExeName\"' with no arguments for the command-line menu.";

}

######################
# Decide what to do.
######################

if [ "$1" = "-i" ]; then
	installAndRun;
elif [ "$1" = "-u" ]; then
	uninstallApp;
else
	echo "This is the $AppName installer. Choose an option:";
	echo # line break;
	COLUMNS=12 # I hate linux;
	select choice in "Install/Update and run $AppName" "Uninstall $AppName" "Cancel";
	do
		case $choice in
			Install* ) installAndRun;break;;
			Uninstall* ) uninstallApp;break;;
			Cancel ) exit;;
		esac
	done
fi
